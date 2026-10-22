using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Sqlite;
using HVO.SkyMonitor.Processing.SpaceWeather;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

public sealed partial class SqliteSwpcStore : ISwpcStore
{
    internal const int SchemaVersion = 1;
    internal const long RawBodyLimit = 256L * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly SwpcFetchCheckpoint EmptyCheckpoint = new(null, null, false, 0, 0, 0, null);
    private readonly SwpcStoreSettings _settings;
    private readonly ILogger<SqliteSwpcStore> _logger;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Dispose drains the gate so late calls observe owner disposal.")]
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Decoded rows reused across operations. They are read and replaced only under the gate, and are
    // dropped whenever a transaction does not commit, so they never describe rows that were rolled back.
    private readonly Dictionary<SwpcProduct, DecodedSnapshot> _decoded = [];
    private readonly Dictionary<long, DecodedMessage> _messages = [];
    private DecodedNotices? _notices;
    private PhysicalRoot? _root;
    private FileStream? _ownership;
    private bool _initialized;
    private bool _disposed;

    public SqliteSwpcStore(SwpcStoreSettings settings, ILogger<SqliteSwpcStore> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        if (!Path.IsPathFullyQualified(settings.RootDirectory)) throw new ArgumentException("The root must be absolute.", nameof(settings));
        foreach (var p in Enum.GetValues<SwpcProduct>())
            if (!settings.Freshness.TryGetValue(p, out var t) || t.BaseInterval <= TimeSpan.Zero || t.OutdatedAfter < t.BaseInterval * 3)
                throw new ArgumentException("Every product requires valid freshness thresholds.", nameof(settings));
        _settings = settings; _logger = logger;
    }

    public async ValueTask InitializeAsync(CancellationToken ct)
        => _ = await ExecuteAsync(null, static (_, _) => true, ct).ConfigureAwait(false);
    public ValueTask<SwpcStoreView> ReadAsync(DateTimeOffset atUtc, CancellationToken ct)
        => ExecuteAsync(atUtc, (db, tx) => ReadView(db, tx, atUtc), ct);

    internal async ValueTask<T> ExecuteAsync<T>(DateTimeOffset? atUtc, Func<SqliteConnection, SqliteTransaction, T> action, CancellationToken ct)
    {
        if (atUtc is { } time) RequireUtc(time);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_ownership is null) AcquireRoot();
            using var db = await OpenAsync(ct).ConfigureAwait(false);
            if (!_initialized) { VerifySchema(db); _initialized = true; StoreInitialized(_logger, SchemaVersion); }
            return InTransaction(db, atUtc, action, ct);
        }
        catch (ProjectionFailureException failure)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.Original).Throw();
            throw;
        }
        catch (Exception exception) when ((exception is SqliteException or IOException or UnauthorizedAccessException) && exception is not SwpcStorageException && exception is not InvalidDataException)
        { throw new SwpcStorageException("space-weather.storage-io", "Space-weather storage could not complete durably.", exception); }
        finally { _gate.Release(); }
    }

    private T InTransaction<T>(SqliteConnection db, DateTimeOffset? atUtc, Func<SqliteConnection, SqliteTransaction, T> action, CancellationToken ct)
    {
        var committed = false;
        try
        {
            using var tx = db.BeginTransaction(deferred: false);
            if (atUtc is { } time) Purge(db, tx, time);
            ct.ThrowIfCancellationRequested();
            var result = action(db, tx);
            ct.ThrowIfCancellationRequested(); tx.Commit(); committed = true; return result;
        }
        finally
        {
            if (!committed) { _decoded.Clear(); _messages.Clear(); _notices = null; }
        }
    }
    private void AcquireRoot()
    {
        Directory.CreateDirectory(_settings.RootDirectory);
        _root = PhysicalRoot.Open(_settings.RootDirectory);
        try { _ownership = new FileStream(_root.Resolve("space-weather.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) { throw new SwpcStorageException("space-weather.storage-locked", "Another space-weather store owns the root.", exception); }
    }
    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var root = _root ?? throw new InvalidOperationException("The storage root is not owned.");
        var path = root.Resolve("space-weather.db");
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) root.Verify(path + suffix, "space-weather-open");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 5 }.ToString());
        try
        {
            await SqliteConnectionConfigurationGate.OpenAndConfigureAsync(db, (connection, _) =>
            {
                Exec(connection, null, "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
                if (!string.Equals(Scalar(connection, null, "PRAGMA journal_mode=WAL;") as string, "wal", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Space-weather storage could not enable WAL.");
                return ValueTask.CompletedTask;
            }, ct).ConfigureAwait(false);
            return db;
        }
        catch
        {
            await db.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
    public void Dispose()
    {
        _gate.Wait();
        try { if (_disposed) return; _disposed = true; _ownership?.Dispose(); _ownership = null; }
        finally { _gate.Release(); }
    }
    internal static void RequireUtc(DateTimeOffset value)
    { if (value.Offset != TimeSpan.Zero) throw new ArgumentException("Storage timestamps must be UTC."); }
    internal static string Json<T>(T value) => JsonSerializer.Serialize(value);
    internal static T Parse<T>(string value) => JsonSerializer.Deserialize<T>(value) ?? throw new InvalidDataException("Stored space-weather data is null.");
    internal static T Parse<T>(ReadOnlySpan<byte> value) => JsonSerializer.Deserialize<T>(value) ?? throw new InvalidDataException("Stored space-weather data is null.");
    private static long Ticks(DateTimeOffset value) => value.UtcTicks;
    private static DateTimeOffset Time(long ticks) => new(ticks, TimeSpan.Zero);
    internal static void Bump(SqliteConnection db, SqliteTransaction tx) => Exec(db, tx, "UPDATE swpc_meta SET revision=revision+1 WHERE id=1;");
    private static long Revision(SqliteConnection db, SqliteTransaction tx) => Convert.ToInt64(Scalar(db, tx, "SELECT revision FROM swpc_meta WHERE id=1;"), CultureInfo.InvariantCulture);
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only private constant SQL text is passed; values are parameterized.")]
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static int Exec(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(db, tx, sql, parameters); return command.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(db, tx, sql, parameters); var result = command.ExecuteScalar(); return result is DBNull ? null : result; }
    private static long Count(SqliteConnection db, SqliteTransaction tx, string sql) => Convert.ToInt64(Scalar(db, tx, sql), CultureInfo.InvariantCulture);
}
