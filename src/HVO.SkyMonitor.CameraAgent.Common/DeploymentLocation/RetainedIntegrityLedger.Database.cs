using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

internal sealed partial class RetainedIntegrityLedger
{
    private void ValidateLayout()
    {
        var path = Path.Combine(_directory, DatabaseName);
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return;
        using var read = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        read.Open();
        using var command = read.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND NOT (type='table' AND name IN ('entries','scan_entries','pending','metadata'));";
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidDataException("Retained integrity ledger has an unexpected layout.");
    }

    private void OpenSchema()
    {
        Execute("""
            PRAGMA busy_timeout=100;
            PRAGMA trusted_schema=OFF;
            PRAGMA journal_mode=DELETE;
            PRAGMA synchronous=FULL;
            PRAGMA cache_size=-4096;
            CREATE TABLE IF NOT EXISTS entries(key TEXT PRIMARY KEY,digest BLOB NOT NULL,kind INTEGER NOT NULL,generation INTEGER NOT NULL,auth BLOB NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS scan_entries(key TEXT PRIMARY KEY,digest BLOB NOT NULL,kind INTEGER NOT NULL,generation INTEGER NOT NULL,auth BLOB NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS pending(key TEXT PRIMARY KEY,tree INTEGER NOT NULL,generation INTEGER NOT NULL,planned TEXT,source TEXT,denied INTEGER NOT NULL,auth BLOB NOT NULL) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS metadata(id INTEGER PRIMARY KEY CHECK(id=1),payload BLOB NOT NULL);
            """);
    }

    private void ReadMetadata()
    {
        using var command = _database.CreateCommand();
        command.CommandText = "SELECT payload FROM metadata WHERE id=1 AND length(payload)<=16384;";
        if (command.ExecuteScalar() is not byte[] bytes)
        {
            if (File.Exists(_checkpoint) || Scalar("SELECT COUNT(*) FROM entries;") != 0)
                throw new InvalidDataException("Retained integrity metadata is missing.");
            return;
        }
        var metadata = JsonSerializer.Deserialize<Metadata>(_protector.Unprotect(bytes))
            ?? throw new InvalidDataException("Retained integrity metadata is invalid.");
        if (metadata.Nonce is not { Length: 32 } || metadata.Chain is not { Length: 128 } || metadata.Count < 0 || metadata.Generation < 0 || metadata.Pending is not { Length: 128 })
            throw new InvalidDataException("Retained integrity metadata is invalid.");
        _nonce = metadata.Nonce;
        _generation = metadata.Generation;
        _chain = Convert.FromHexString(metadata.Chain);
        _count = metadata.Count;
        _established = metadata.Established;
        _pendingCommitment = Convert.FromHexString(metadata.Pending);
    }

    private void WriteMetadata(SqliteTransaction transaction)
    {
        _pendingCommitment = ComputePendingRoot(transaction);
        var metadata = CurrentMetadata();
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO metadata(id,payload) VALUES(1,$payload);";
        command.Parameters.AddWithValue("$payload", _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(metadata)));
        command.ExecuteNonQuery();
    }

    private void ReadIntents()
    {
        _pending.Clear();
        using var command = _database.CreateCommand();
        command.CommandText = "SELECT key,tree,generation,planned,source,denied,auth FROM pending;";
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            var intent = new Intent(rows.GetString(0), rows.GetBoolean(1), rows.GetInt64(2),
                rows.IsDBNull(3) ? null : rows.GetString(3), rows.IsDBNull(4) ? null : rows.GetString(4), rows.GetBoolean(5));
            var proof = JsonSerializer.Deserialize<IntentProof>(_protector.Unprotect((byte[])rows.GetValue(6)));
            if (proof != new IntentProof(_nonce, intent.Key, intent.Tree, intent.Generation, intent.Planned, intent.Source, intent.Denied))
                throw new InvalidDataException("Retained mutation intent authentication failed.");
            if (intent.Generation > _generation) throw new InvalidDataException("Retained mutation generation is invalid.");
            _pending.Add(intent);
        }
        if (!ComputePendingRoot().AsSpan().SequenceEqual(_pendingCommitment))
            throw new InvalidDataException("Retained mutation intents failed authentication.");
    }

    private Metadata CurrentMetadata() => new(_nonce, _generation, Convert.ToHexString(_chain), _count, _established,
        Convert.ToHexString(_pendingCommitment));

    private void ValidateLiveMetadata(SqliteTransaction? transaction = null, long? generation = null)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT payload FROM metadata WHERE id=1 AND length(payload)<=16384;";
        var value = command.ExecuteScalar();
        if (value is null && !_established && (generation ?? _generation) == 0) return;
        if (value is not byte[] bytes || JsonSerializer.Deserialize<Metadata>(_protector.Unprotect(bytes)) != (CurrentMetadata() with { Generation = generation ?? _generation }) ||
            !ComputePendingRoot(transaction).AsSpan().SequenceEqual(_pendingCommitment))
            throw new InvalidDataException("Retained integrity metadata authentication failed.");
    }

    private void ValidatePathMetadata()
    {
        // A live SQLite handle may still address an unlinked/replaced inode. Check
        // the durable pathname before qualifying a checkpoint, not only that handle.
        var path = Path.Combine(_directory, DatabaseName);
        HVO.SkyMonitor.CameraAgent.Common.RawIngress.RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
        using var current = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        current.Open();
        using var command = current.CreateCommand();
        command.CommandText = "SELECT payload FROM metadata WHERE id=1 AND length(payload)<=16384;";
        var value = command.ExecuteScalar();
        if (value is null && !_established && _generation == 0) return;
        if (value is not byte[] bytes || JsonSerializer.Deserialize<Metadata>(_protector.Unprotect(bytes)) != CurrentMetadata())
            throw new InvalidDataException("Retained integrity durable metadata authentication failed.");
    }

    private byte[] ComputePendingRoot(SqliteTransaction? transaction = null)
    {
        var root = new byte[64];
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT key,tree,generation,planned,source,denied,auth FROM pending;";
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            var expected = new IntentProof(_nonce, rows.GetString(0), rows.GetBoolean(1), rows.GetInt64(2),
                rows.IsDBNull(3) ? null : rows.GetString(3), rows.IsDBNull(4) ? null : rows.GetString(4), rows.GetBoolean(5));
            if (JsonSerializer.Deserialize<IntentProof>(_protector.Unprotect((byte[])rows.GetValue(6))) != expected)
                throw new InvalidDataException("Retained mutation intent authentication failed.");
            Accumulate(root, SHA512.HashData(JsonSerializer.SerializeToUtf8Bytes(expected)));
        }
        return root;
    }

    private void SaveIntent(Intent intent)
    {
        using var transaction = _database.BeginTransaction();
        ValidateLiveMetadata(transaction, _generation - 1);
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO pending(key,tree,generation,planned,source,denied,auth) VALUES($key,$tree,$gen,$planned,$source,$denied,$auth);";
        command.Parameters.AddWithValue("$key", intent.Key);
        command.Parameters.AddWithValue("$tree", intent.Tree);
        command.Parameters.AddWithValue("$gen", intent.Generation);
        command.Parameters.AddWithValue("$planned", (object?)intent.Planned ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", (object?)intent.Source ?? DBNull.Value);
        command.Parameters.AddWithValue("$denied", intent.Denied);
        command.Parameters.AddWithValue("$auth", _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(
            new IntentProof(_nonce, intent.Key, intent.Tree, intent.Generation, intent.Planned, intent.Source, intent.Denied))));
        command.ExecuteNonQuery();
        WriteMetadata(transaction);
        transaction.Commit();
    }

    private void RemoveIntent(Intent intent, SqliteTransaction transaction)
    {
        if (intent.Tree && intent.Key == "J|" || _pending.Any(pending => pending.Key == intent.Key && pending.Denied)) return; // bulk journal changes are certified by a full scrub.
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM pending WHERE key=$key;";
        command.Parameters.AddWithValue("$key", intent.Key);
        command.ExecuteNonQuery();
        _pending.RemoveAll(pending => pending.Key == intent.Key);
    }

    private Entry? ReadEntry(string key, SqliteTransaction? transaction = null)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT digest,kind,generation,auth FROM entries WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        using var row = command.ExecuteReader();
        if (!row.Read()) return null;
        var entry = new Entry(key, (byte[])row.GetValue(0), row.GetInt32(1), row.GetInt64(2));
        var proof = JsonSerializer.Deserialize<EntryProof>(_protector.Unprotect((byte[])row.GetValue(3)));
        if (proof != new EntryProof(_nonce, key, Convert.ToHexString(entry.Digest), entry.Kind, entry.Generation))
            throw new InvalidDataException("Retained integrity entry authentication failed.");
        return entry;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Both possible statements are fixed internal SQL; keys and values are parameters.")]
    private void Upsert(string table, Entry entry, SqliteTransaction? transaction = null)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        // Both statements are fixed SQL, never a caller-supplied table or identifier.
        command.CommandText = table == "entries"
            ? "INSERT OR REPLACE INTO entries(key,digest,kind,generation,auth) VALUES($key,$digest,$kind,$gen,$auth);"
            : "INSERT OR REPLACE INTO scan_entries(key,digest,kind,generation,auth) VALUES($key,$digest,$kind,$gen,$auth);";
        command.Parameters.AddWithValue("$key", entry.Key);
        command.Parameters.AddWithValue("$digest", entry.Digest);
        command.Parameters.AddWithValue("$kind", entry.Kind);
        command.Parameters.AddWithValue("$gen", entry.Generation);
        command.Parameters.AddWithValue("$auth", _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(
            new EntryProof(_nonce, entry.Key, Convert.ToHexString(entry.Digest), entry.Kind, entry.Generation))));
        command.ExecuteNonQuery();
    }

    // A set commitment detects authenticated-row deletion/replay as well as changed bytes.
    // SHA-512 leaves include unique keys, protected nonce, kind and generation.
    // Addition modulo 2^512 supports bounded updates without XOR's linear replay
    // relations. The commitment is authenticated with metadata/checkpoint; full
    // scrubs authenticate every row and recompute the complete set commitment.
    private byte[] EntryHash(Entry entry) => SHA512.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new EntryProof(_nonce, entry.Key, Convert.ToHexString(entry.Digest), entry.Kind, entry.Generation)));

    private static void Accumulate(byte[] accumulator, byte[] leaf, bool remove = false)
    {
        var carry = 0;
        for (var i = 0; i < accumulator.Length; i++)
        {
            var sum = accumulator[i] + (remove ? -leaf[i] : leaf[i]) + carry;
            accumulator[i] = unchecked((byte)sum);
            carry = sum >> 8;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Both statements are fixed internal SQL.")]
    private byte[] ComputeEntriesRoot(string table, SqliteTransaction? transaction = null, CancellationToken cancellationToken = default)
    {
        var root = new byte[64];
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = table == "entries"
            ? "SELECT key,digest,kind,generation,auth FROM entries;"
            : "SELECT key,digest,kind,generation,auth FROM scan_entries;";
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = new Entry(rows.GetString(0), (byte[])rows.GetValue(1), rows.GetInt32(2), rows.GetInt64(3));
            var proof = JsonSerializer.Deserialize<EntryProof>(_protector.Unprotect((byte[])rows.GetValue(4)));
            if (proof != new EntryProof(_nonce, entry.Key, Convert.ToHexString(entry.Digest), entry.Kind, entry.Generation))
                throw new InvalidDataException("Retained integrity entry authentication failed.");
            Accumulate(root, EntryHash(entry));
        }
        return root;
    }

    private void DeleteStage(string key)
    {
        using var command = _database.CreateCommand();
        command.CommandText = "DELETE FROM scan_entries WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "SQL is supplied only by fixed private schema/transaction statements.")]
    private void Execute(string sql, SqliteTransaction? transaction = null)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The only caller supplies a fixed count query.")]
    private long Scalar(string sql, SqliteTransaction? transaction = null)
    {
        using var command = _database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
