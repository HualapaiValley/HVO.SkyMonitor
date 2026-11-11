using System.Security.Cryptography;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

// Disposable parse proofs only. Every caller hashes current bytes, including cache hits.
// Bounded transactions persist independently validated prefixes; none certifies a whole scan.
internal sealed class RetainedManifestValidationIndex : IDisposable
{
    internal const string FileName = "retained-validation.v2.db";
    internal const int BatchSize = 128;
    private const int ProofLength = 73;
    private readonly byte[] _identity;
    private readonly IDeploymentLocationProtector _protector;
    private readonly Action _unavailable;
    private readonly Action<string>? _beforeCommit;
    private readonly string _path;
    private readonly long _createdUtc;
    private readonly Dictionary<string, byte[]> _pending = new(StringComparer.Ordinal);
    private SqliteConnection? _connection;
    private SqliteCommand? _lookup;
    private bool _warned;
    private bool _disposed;

    internal RetainedManifestValidationIndex(string directory, byte[] identity,
        IDeploymentLocationProtector protector, DateTimeOffset now,
        Action unavailable, Action<string>? beforeCommit = null)
    {
        _identity = identity;
        _protector = protector;
        _unavailable = unavailable;
        _beforeCommit = beforeCommit;
        _path = Path.Combine(directory, FileName);
        _createdUtc = now.ToUnixTimeMilliseconds();
        try
        {
            EnsureConfined();
            try { ValidateExistingLayout(); Open(); }
            catch (InvalidDataException) { Recreate(); }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 11 or 26) { Recreate(); }
        }
        catch (Exception exception) when (IsOptionalFailure(exception)) { Disable(); }
    }

    internal enum ProofKind : byte { Artifact = 1, OtherJson = 2 }

    internal static byte[] Hash(ReadOnlySpan<byte> bytes) => SHA256.HashData(bytes);

    internal ProofKind? Find(byte[] digest)
    {
        var key = Convert.ToHexString(digest);
        if (_pending.TryGetValue(key, out var pending)) return ReadProof(pending, digest);
        if (_lookup is null) return null;
        try
        {
            _lookup.Parameters[0].Value = digest;
            if (_lookup.ExecuteScalar() is not byte[] bytes) return null;
            try { return ReadProof(_protector.Unprotect(bytes), digest); }
            catch (CryptographicException) { return null; }
        }
        catch (Exception exception) when (IsOptionalFailure(exception)) { Disable(); return null; }
    }

    internal void Record(byte[] digest, ProofKind kind)
    {
        if (_connection is null) return;
        var bytes = new byte[ProofLength];
        "HVORP002"u8.CopyTo(bytes);
        _identity.CopyTo(bytes, 8);
        digest.CopyTo(bytes, 40);
        bytes[72] = (byte)kind;
        _pending[Convert.ToHexString(digest)] = bytes;
        if (_pending.Count >= BatchSize) Flush();
    }

    private ProofKind? ReadProof(byte[] bytes, byte[] digest)
    {
        if (bytes.Length != ProofLength || !bytes.AsSpan(0, 8).SequenceEqual("HVORP002"u8) ||
            !bytes.AsSpan(8, 32).SequenceEqual(_identity) || !bytes.AsSpan(40, 32).SequenceEqual(digest)) return null;
        return bytes[72] is (byte)ProofKind.Artifact or (byte)ProofKind.OtherJson ? (ProofKind)bytes[72] : null;
    }

    private void ValidateExistingLayout()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length == 0) return;
        using var read = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        read.Open();
        using var schema = read.CreateCommand();
        schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND NOT " +
            "(type='table' AND name='proofs' OR type='index' AND name='proof_expiration');";
        if (Convert.ToInt64(schema.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidDataException("The advisory index has an unexpected layout.");
        schema.CommandText = "PRAGMA table_info(proofs);";
        using var columns = schema.ExecuteReader();
        foreach (var (name, type) in new[] { ("digest", "BLOB"), ("proof", "BLOB"), ("created_utc", "INTEGER") })
            if (!columns.Read() || columns.GetString(1) != name || columns.GetString(2) != type || columns.GetInt32(3) != 1)
                throw new InvalidDataException("The advisory index has an unexpected layout.");
        if (columns.Read()) throw new InvalidDataException("The advisory index has an unexpected layout.");
    }

    private void Recreate()
    {
        Close();
        // Remove only the dedicated advisory names. A foreign DB/hard link is never opened writable.
        foreach (var suffix in new[] { "", "-journal" }) File.Delete(_path + suffix);
        Open();
    }

    private void Open()
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        _connection.Open();
        using var setup = _connection.CreateCommand();
        Sqlite.SqliteConnectionConfigurationGate.Run(() =>
        {
            setup.CommandText = "PRAGMA busy_timeout=100;";
            setup.ExecuteNonQuery();
            setup.CommandText = "PRAGMA trusted_schema=OFF;";
            setup.ExecuteNonQuery();
            setup.CommandText = "PRAGMA journal_mode=DELETE;";
            setup.ExecuteNonQuery();
            setup.CommandText = "PRAGMA synchronous=FULL;";
            setup.ExecuteNonQuery();
            setup.CommandText = "PRAGMA cache_size=-4096;";
            setup.ExecuteNonQuery();
        });
        setup.CommandText = """
            CREATE TABLE IF NOT EXISTS proofs (
                digest BLOB PRIMARY KEY NOT NULL,
                proof BLOB NOT NULL,
                created_utc INTEGER NOT NULL
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS proof_expiration ON proofs(created_utc);
            """;
        setup.ExecuteNonQuery();
        _lookup = _connection.CreateCommand();
        _lookup.CommandText = "SELECT proof FROM proofs WHERE digest=$digest AND typeof(proof)='blob' AND length(proof)<=4096;";
        _lookup.Parameters.Add("$digest", SqliteType.Blob);
        _lookup.Prepare();
        // Expiration only removes acceleration. Limit cleanup work and never traverse evidence.
        using var prune = _connection.CreateCommand();
        prune.CommandText = "DELETE FROM proofs WHERE digest IN (SELECT digest FROM proofs WHERE created_utc < $cutoff LIMIT 128);";
        prune.Parameters.AddWithValue("$cutoff", _createdUtc - (long)TimeSpan.FromDays(30).TotalMilliseconds);
        prune.ExecuteNonQuery();
    }

    private void Flush()
    {
        if (_connection is null || _pending.Count == 0) return;
        try
        {
            EnsureConfined();
            using var transaction = _connection.BeginTransaction();
            using var insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR REPLACE INTO proofs(digest,proof,created_utc) VALUES($digest,$proof,$created);";
            insert.Parameters.Add("$digest", SqliteType.Blob);
            insert.Parameters.Add("$proof", SqliteType.Blob);
            insert.Parameters.AddWithValue("$created", _createdUtc);
            insert.Prepare();
            foreach (var (key, plaintext) in _pending)
            {
                insert.Parameters[0].Value = Convert.FromHexString(key);
                insert.Parameters[1].Value = _protector.Protect(plaintext);
                insert.ExecuteNonQuery();
            }
            using var prune = _connection.CreateCommand();
            prune.Transaction = transaction;
            prune.CommandText = "DELETE FROM proofs WHERE digest IN (SELECT digest FROM proofs WHERE created_utc < $cutoff LIMIT 128);";
            prune.Parameters.AddWithValue("$cutoff", _createdUtc - (long)TimeSpan.FromDays(30).TotalMilliseconds);
            prune.ExecuteNonQuery();
            _beforeCommit?.Invoke(_path);
            transaction.Commit();
            _pending.Clear();
        }
        catch (Exception exception) when (IsOptionalFailure(exception)) { Disable(); }
    }

    private void EnsureConfined()
    {
        var directory = Path.GetDirectoryName(_path)!;
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
            RawIngressFileStore.EnsureNoSymbolicLinks(directory, _path + suffix);
    }

    private static bool IsOptionalFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or SqliteException or CryptographicException;

    private void Disable()
    {
        _pending.Clear();
        Close();
        Warn();
    }

    private void Warn()
    {
        if (_warned) return;
        _warned = true;
        _unavailable();
    }

    private void Close()
    {
        var lookup = _lookup;
        var connection = _connection;
        _lookup = null;
        _connection = null;
        try { lookup?.Dispose(); }
        catch (Exception exception) when (IsOptionalFailure(exception)) { Warn(); }
        try { connection?.Dispose(); }
        catch (Exception exception) when (IsOptionalFailure(exception)) { Warn(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Cancellation/failure cannot invalidate proofs for other fully checked current bytes.
        // The small prefix commit has a finite SQLite lock budget, independent of caller cancellation.
        Flush();
        Close();
    }
}
