using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

// Expected content and managed mutation intents, not cached installation identity.
// A process epoch is qualified only by a complete scrub; persisted readiness is never inherited.
internal sealed partial class RetainedIntegrityLedger : IDisposable
{
    internal const string DatabaseName = "retained-integrity.v1.db";
    internal const string CheckpointName = "retained-integrity.v1.protected";
    private static readonly ConcurrentDictionary<string, RetainedIntegrityLedger> Roots = new(StringComparer.Ordinal);
    private static readonly AsyncLocal<Mutation?> Current = new();
    private readonly string _root;
    private readonly string _directory;
    private readonly string _checkpoint;
    private readonly IDeploymentLocationProtector _protector;
    private readonly Func<string, Entry?> _readCurrent;
    private readonly Action _unqualifiedWarning;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset _scrubDue;
    private readonly SemaphoreSlim _barrier = new(1, 1);
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private readonly SqliteConnection _database;
    private readonly FileStream _ownership;
    private readonly List<Intent> _pending = [];
    private string _nonce = Guid.NewGuid().ToString("N");
    private byte[] _binding = [];
    private byte[] _chain = new byte[64];
    private byte[] _pendingCommitment = new byte[64];
    private long _generation;
    private long _count;
    private bool _established;
    private bool _qualified;
    private bool _disposed;

    internal sealed record Entry(string Key, byte[] Digest, int Kind, long Generation = 0);
    private sealed record EntryProof(string Nonce, string Key, string Digest, int Kind, long Generation);
    private sealed record Intent(string Key, bool Tree, long Generation, string? Planned = null, string? Source = null, bool Denied = false);
    private sealed record IntentProof(string Nonce, string Key, bool Tree, long Generation, string? Planned, string? Source, bool Denied);
    private sealed record Metadata(string Nonce, long Generation, string Chain, long Count, bool Established, string Pending);
    private sealed record Checkpoint(string Epoch, string Nonce, string Binding, long Generation, string Chain, long Count, bool Qualified);

    internal RetainedIntegrityLedger(string root, IDeploymentLocationProtector protector,
        Func<string, Entry?> readCurrent, Action unqualifiedWarning, TimeProvider timeProvider)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        _directory = Path.Combine(_root, ".location");
        _checkpoint = Path.Combine(_directory, CheckpointName);
        _protector = protector;
        _readCurrent = readCurrent;
        _unqualifiedWarning = unqualifiedWarning;
        _timeProvider = timeProvider;
        using var registration = RegistrationLease.Enter(registering: true);
        Directory.CreateDirectory(_directory);
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, Path.Combine(_directory, DatabaseName + suffix));
        _database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_directory, DatabaseName),
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 1
        }.ToString());
        var ownershipPath = Path.Combine(_directory, "retained-integrity.v1.lock");
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, ownershipPath);
        try { _ownership = new FileStream(ownershipPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception)
        {
            _database.Dispose();
            throw new InvalidOperationException("The retained root already has a live integrity owner.", exception);
        }
        try
        {
            ValidateLayout();
            _database.Open();
            OpenSchema();
            ReadMetadata();
            ReadIntents();
            if (!Roots.TryAdd(_root, this))
                throw new InvalidOperationException("The retained root already has an integrity owner in this process.");
        }
        catch { _database.Dispose(); _ownership.Dispose(); throw; }
        // The old process's checkpoint is deliberately untrusted, even on a clean restart.
    }

    internal bool RequiresScrub => !Volatile.Read(ref _qualified);
    internal static string FileKey(string root, string path) => "F|" + Path.GetRelativePath(root, path);
    internal static string JournalKey(string capture) => "J|" + capture;

    internal async ValueTask<bool> VerifyAsync(byte[] binding, CancellationToken cancellationToken)
    {
        if (!await _barrier.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            if (!_qualified) return false;
            if (_timeProvider.GetUtcNow() >= _scrubDue) return Invalidate();
            if (!_binding.AsSpan().SequenceEqual(binding)) return Invalidate();
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, _checkpoint);
            using var stream = File.OpenRead(_checkpoint);
            if (stream.Length > 16_384) return Invalidate();
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var stored = JsonSerializer.Deserialize<Checkpoint>(_protector.Unprotect(bytes));
            return stored == Snapshot(qualified: true) || Invalidate();
        }
        catch (Exception exception) when (OptionalIo(exception)) { return Invalidate(); }
        finally { _barrier.Release(); }
    }

    private bool Invalidate()
    {
        _qualified = false;
        _unqualifiedWarning();
        return false;
    }

    internal Scrub BeginScrub(byte[] binding, Func<string, Entry?> readForScrub, bool strict, CancellationToken cancellationToken)
    {
        _barrier.Wait(cancellationToken);
        try
        {
            _qualified = false;
            ValidateLiveMetadata();
            ValidatePathMetadata();
            if (_established && !ComputeEntriesRoot("entries", cancellationToken: cancellationToken).AsSpan().SequenceEqual(_chain))
                throw new InvalidDataException("Retained integrity ledger authentication failed.");
            Execute("DELETE FROM scan_entries;");
            return new Scrub(this, binding, readForScrub, _generation, strict);
        }
        finally { _barrier.Release(); }
    }

    internal sealed class Scrub(RetainedIntegrityLedger owner, byte[] binding,
        Func<string, Entry?> readCurrent, long startGeneration, bool strict) : IDisposable
    {
        private bool _finished;
        private bool _locked;
        private bool _invalid;
        private bool _drift;
        private bool _disposed;
        private readonly List<Entry> _batch = [];
        private void Flush()
        {
            if (_batch.Count == 0) return;
            using var transaction = owner._database.BeginTransaction();
            foreach (var entry in _batch) owner.Upsert("scan_entries", entry, transaction);
            transaction.Commit();
            _batch.Clear();
        }
        internal IDisposable Observe(CancellationToken cancellationToken)
        {
            owner._barrier.Wait(cancellationToken);
            return new Release(owner._barrier);
        }

        // Called while Observe holds the short per-entry mutation barrier.
        internal void Record(Entry entry)
        {
            var expected = owner.ReadEntry(entry.Key);
            if (owner._established && !owner.AllowedPending(entry.Key, entry.Digest) &&
                (expected is null || expected.Kind == 0 || !expected.Digest.AsSpan().SequenceEqual(entry.Digest)))
                Drift();
            _invalid |= entry.Kind == 3;
            _batch.Add(entry with { Generation = expected?.Generation ?? startGeneration });
            if (_batch.Count >= 128) Flush();
        }

        private void Drift()
        {
            if (strict) throw new InvalidDataException("Retained integrity drift requires recovery before verification.");
            if (!_drift) owner._unqualifiedWarning();
            _drift = true;
        }

        internal void Finish(CancellationToken cancellationToken)
        {
            owner._barrier.Wait(cancellationToken);
            _locked = true;
            Flush();
            // Re-read only keys changed during the scrub or left by an interrupted managed write.
            // A read after an in-band write already compared against that write's authenticated digest.
            using var query = owner._database.CreateCommand();
            query.CommandText = "SELECT key FROM entries WHERE generation>$start UNION SELECT key FROM entries WHERE kind<>0 AND key NOT IN (SELECT key FROM scan_entries);";
            query.Parameters.AddWithValue("$start", startGeneration);
            var changed = new List<string>();
            using (var reader = query.ExecuteReader()) while (reader.Read()) changed.Add(reader.GetString(0));
            foreach (var key in changed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expected = owner.ReadEntry(key);
                var actual = readCurrent(key);
                if (actual is null)
                {
                    if (expected is { Kind: not 0 } && !owner.AllowedPending(key, null))
                        Drift();
                    owner.DeleteStage(key);
                }
                else
                {
                    Record(actual);
                }
            }
            Flush();
            _invalid = owner.Scalar("SELECT COUNT(*) FROM scan_entries WHERE kind=3;") != 0;
            // No new writer can enter between this final validation and the authority publication.
        }

        internal void Publish()
        {
            if (!_locked) throw new InvalidOperationException("A scrub must finish before publication.");
            if (_drift) return; // Startup/recovery stays available, but unexplained changes never replace expected evidence.
            using var transaction = owner._database.BeginTransaction();
            owner.Execute("DELETE FROM entries; INSERT INTO entries SELECT * FROM scan_entries; DELETE FROM scan_entries; DELETE FROM pending;", transaction);
            owner._pending.Clear();
            owner._generation++;
            owner._binding = binding;
            owner._count = owner.Scalar("SELECT COUNT(*) FROM entries WHERE kind<>0;", transaction);
            owner._chain = owner.ComputeEntriesRoot("entries", transaction);
            owner._established = true;
            owner.WriteMetadata(transaction);
            transaction.Commit();
            owner.ValidatePathMetadata();
            owner._qualified = !_invalid;
            owner._scrubDue = owner._timeProvider.GetUtcNow().AddHours(24);
            owner.WriteCheckpoint();
            _finished = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_finished) owner._qualified = false;
            if (_locked) { _locked = false; owner._barrier.Release(); }
        }
    }

    internal static IDisposable BeginPublication(string source, string destination)
    {
        var plans = new List<(string Path, bool Tree, string? Planned, string? Source)>();
        if (Tracked(source)) plans.Add((source, false, null, null));
        if (Tracked(destination)) plans.Add((destination, false, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))), null));
        return plans.Count == 0 ? Empty.Instance : Begin(plans);
    }
    internal static IDisposable BeginWrite(string path, ReadOnlyMemory<byte> bytes)
        => Tracked(path) ? Begin([(path, false, Convert.ToHexString(SHA256.HashData(bytes.Span)), null)]) : Empty.Instance;
    internal static IDisposable BeginDelete(string path) => Tracked(path) ? Begin([(path, false, null, null)]) : Empty.Instance;
    internal static IDisposable BeginTrees(params string[] paths) => Begin(paths.Select(path => (path, true, (string?)null, (string?)null)));
    internal static IDisposable BeginTreeMove(string source, string destination)
        => Begin([(source, true, null, null), (destination, true, null, Path.GetFullPath(source))]);
    internal static IDisposable BeginJournal(string databasePath, string? capture = null, byte[]? manifest = null)
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(databasePath)))!;
        var registration = RegistrationLease.Enter();
        try
        {
            if (!Roots.TryGetValue(root, out var owner)) return registration;
            var scope = owner.Enter([new Intent(capture is null ? "J|" : JournalKey(capture), capture is null, 0,
                manifest is null ? null : Convert.ToHexString(SHA256.HashData(manifest)))]);
            registration.Dispose();
            return scope;
        }
        catch { registration.Dispose(); throw; }
    }
    private static bool Tracked(string path) => path.EndsWith(".json", StringComparison.Ordinal);

    private static Multiple Begin(IEnumerable<(string Path, bool Tree, string? Planned, string? Source)> plans)
    {
        var registration = RegistrationLease.Enter();
        var grouped = new Dictionary<RetainedIntegrityLedger, List<Intent>>();
        try
        {
            foreach (var plan in plans)
            {
                var full = Path.GetFullPath(plan.Path);
                var current = full;
                while (current is not null && !Roots.ContainsKey(current)) current = Path.GetDirectoryName(current);
                if (current is null || !Roots.TryGetValue(current, out var owner)) continue;
                var relative = Path.GetRelativePath(owner._root, full);
                if (relative == "." && plan.Tree) throw new InvalidOperationException("A running retained integrity root cannot be removed.");
                if (!plan.Tree && !Tracked(relative)) continue;
                string? sourceKey = null;
                if (plan.Source is not null)
                {
                    if (!plan.Source.StartsWith(owner._root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidOperationException("Live cross-root tree imports require a complete qualification.");
                    sourceKey = FileKey(owner._root, plan.Source) + Path.DirectorySeparatorChar;
                }
                var intent = new Intent(FileKey(owner._root, full) + (plan.Tree ? Path.DirectorySeparatorChar : string.Empty),
                    plan.Tree, 0, plan.Planned, sourceKey);
                if (!grouped.TryGetValue(owner, out var list)) grouped[owner] = list = [];
                list.Add(intent);
            }
        }
        catch { registration.Dispose(); throw; }
        if (grouped.Count == 0) return new Multiple([registration]);
        var scopes = new List<IDisposable>();
        try
        {
            foreach (var (owner, intents) in grouped.OrderBy(pair => pair.Key._root, StringComparer.Ordinal)) scopes.Add(owner.Enter(intents));
            registration.Dispose();
            return new Multiple(scopes);
        }
        catch
        {
            foreach (var scope in scopes.AsEnumerable().Reverse()) scope.Dispose();
            registration.Dispose();
            throw;
        }
    }

    private IDisposable Enter(IEnumerable<Intent> intents)
    {
        var current = Current.Value;
        if (current is not null && current.Owner == this && current.Active)
        {
            current.Add(intents);
            return new Nested(current);
        }
        _barrier.Wait();
        var mutation = new Mutation(this, current);
        try
        {
            ValidateLiveMetadata();
            mutation.Add(intents);
            Current.Value = mutation;
            return mutation;
        }
        catch
        {
            _qualified = false;
            try { ReadMetadata(); ReadIntents(); }
            catch (Exception exception) when (OptionalIo(exception)) { _unqualifiedWarning(); }
            _barrier.Release();
            throw;
        }
    }

    private sealed class Mutation(RetainedIntegrityLedger owner, Mutation? parent) : IDisposable
    {
        private readonly Dictionary<string, Intent> _intents = new(StringComparer.Ordinal);
        private int _depth = 1;
        internal RetainedIntegrityLedger Owner => owner;
        internal bool Active => _depth > 0;
        internal void Add(IEnumerable<Intent> intents)
        {
            var plans = intents.ToArray();
            var preservesQuarantine = plans.Any(plan => plan.Key.StartsWith("F|quarantine" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && (plan.Planned is not null || plan.Source is not null));
            lock (_intents)
            {
                foreach (var intent in plans)
                {
                    if (_intents.TryGetValue(intent.Key, out var previous) && previous == intent) continue;
                    _intents[intent.Key] = intent;
                    owner._generation++;
                    var pending = intent with { Generation = owner._generation, Denied = owner.UnexpectedBefore(intent) };
                    owner.SaveIntent(pending);
                    owner._pending.RemoveAll(existing => existing.Key == pending.Key);
                    owner._pending.Add(pending);
                    if (pending.Denied && !preservesQuarantine)
                        throw new InvalidDataException("Unexpected retained changes require recovery before managed replacement or deletion.");
                }
                owner.WriteCheckpoint(qualified: false);
            }
        }
        internal void Nest() => Interlocked.Increment(ref _depth);
        public void Dispose()
        {
            if (Interlocked.Decrement(ref _depth) != 0) return;
            try
            {
                using var transaction = owner._database.BeginTransaction();
                owner.ValidateLiveMetadata(transaction);
                foreach (var intent in _intents.Values) owner.Apply(intent, transaction);
                foreach (var intent in _intents.Values) owner.RemoveIntent(intent, transaction);
                owner.WriteMetadata(transaction);
                transaction.Commit();
                owner.ValidatePathMetadata();
                owner.WriteCheckpoint();
            }
            catch (Exception exception) when (OptionalIo(exception))
            {
                owner.Invalidate();
                try { owner.ReadMetadata(); owner.ReadIntents(); }
                catch (Exception readFailure) when (OptionalIo(readFailure)) { owner.Invalidate(); }
            }
            finally
            {
                if (Current.Value == this) Current.Value = parent;
                owner._barrier.Release();
            }
        }
    }

    private void Apply(Intent intent, SqliteTransaction transaction)
    {
        if (_pending.Any(pending => pending.Key == intent.Key && pending.Denied))
        {
            _qualified = false;
            return;
        }
        if (!intent.Tree)
        {
            var actual = _readCurrent(intent.Key);
            var previous = ReadEntry(intent.Key, transaction);
            var unchanged = actual is null ? previous is null or { Kind: 0 }
                : previous is { Kind: not 0 } && previous.Digest.AsSpan().SequenceEqual(actual.Digest);
            if (!unchanged && !AllowedPending(intent.Key, actual?.Digest, transaction))
                throw new InvalidDataException("A managed update did not publish its declared integrity result.");
            SaveChanged(actual ?? new Entry(intent.Key, [], 0), transaction);
            return;
        }
        if (intent.Key == "J|")
        {
            _qualified = false;
            return;
        }
        // Tree moves/deletions affect only this prefix. Never enumerate the whole retained root.
        using (var mark = _database.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "SELECT key FROM entries WHERE key >= $prefix AND key < $end;";
            mark.Parameters.AddWithValue("$prefix", intent.Key);
            mark.Parameters.AddWithValue("$end", PrefixEnd(intent.Key));
            var old = new List<string>();
            using (var rows = mark.ExecuteReader()) while (rows.Read()) old.Add(rows.GetString(0));
            foreach (var key in old)
            {
                var actual = _readCurrent(key);
                if (actual is null) SaveChanged(new Entry(key, [], 0), transaction);
            }
        }
        if (intent.Key.StartsWith("J|", StringComparison.Ordinal))
        {
            _qualified = false; // migrations/bulk journal recovery require a complete scrub.
            return;
        }
        var directory = Path.Combine(_root, intent.Key[2..].TrimEnd(Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
        {
            var actual = _readCurrent(FileKey(_root, path));
            if (actual is not null)
            {
                var previous = ReadEntry(actual.Key, transaction);
                if ((previous is null || !previous.Digest.AsSpan().SequenceEqual(actual.Digest)) && !AllowedPending(actual.Key, actual.Digest, transaction))
                    throw new InvalidDataException("A managed tree update did not publish its declared integrity result.");
                SaveChanged(actual, transaction);
            }
        }
    }

    private void SaveChanged(Entry entry, SqliteTransaction transaction)
    {
        var previous = ReadEntry(entry.Key, transaction);
        if (previous is { Kind: not 0 }) _count--;
        if (entry.Kind != 0) _count++;
        if (entry.Kind == 3) _qualified = false;
        entry = entry with { Generation = _generation, Digest = entry.Kind == 0 ? previous?.Digest ?? [] : entry.Digest };
        if (previous is not null) Accumulate(_chain, EntryHash(previous), remove: true);
        Upsert("entries", entry, transaction);
        Accumulate(_chain, EntryHash(entry));
    }

    private bool AllowedPending(string key, byte[]? digest, SqliteTransaction? transaction = null)
    {
        foreach (var intent in _pending)
        {
            if (intent.Denied || (intent.Tree ? !key.StartsWith(intent.Key, StringComparison.Ordinal) : key != intent.Key)) continue;
            if (digest is null && intent.Planned is null) return true;
            if (digest is not null && intent.Planned == Convert.ToHexString(digest)) return true;
            if (digest is not null && intent.Source is not null)
            {
                var original = ReadEntry(intent.Source + key[intent.Key.Length..], transaction);
                if (original is not null && original.Digest.AsSpan().SequenceEqual(digest)) return true;
            }
        }
        return false;
    }

    private bool UnexpectedBefore(Intent intent)
    {
        if (!_established) return false;
        if (intent.Tree)
        {
            if (intent.Key == "J|") return false;
            var directory = Path.Combine(_root, intent.Key[2..].TrimEnd(Path.DirectorySeparatorChar));
            var denied = false;
            using var query = _database.CreateCommand();
            query.CommandText = "SELECT key FROM entries WHERE kind<>0 AND key >= $prefix AND key < $end;";
            query.Parameters.AddWithValue("$prefix", intent.Key);
            query.Parameters.AddWithValue("$end", PrefixEnd(intent.Key));
            var keys = new HashSet<string>(StringComparer.Ordinal);
            using (var rows = query.ExecuteReader()) while (rows.Read()) keys.Add(rows.GetString(0));
            if (Directory.Exists(directory))
                foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories))
                    keys.Add(FileKey(_root, path));
            foreach (var key in keys) denied |= UnexpectedBefore(new Intent(key, false, intent.Generation));
            return denied;
        }
        var current = _readCurrent(intent.Key);
        var expected = ReadEntry(intent.Key);
        var changed = current is null ? expected is { Kind: not 0 }
            : expected is null or { Kind: 0 } || !expected.Digest.AsSpan().SequenceEqual(current.Digest);
        if (changed && expected is not null && intent.Planned == Convert.ToHexString(expected.Digest)) return false;
        if (changed) Invalidate();
        return changed;
    }
    private static string PrefixEnd(string prefix) => prefix[..^1] + (char)(prefix[^1] + 1);
    private Checkpoint Snapshot(bool qualified) => new(_epoch, _nonce, Convert.ToHexString(_binding),
        _generation, Convert.ToHexString(_chain), _count, qualified);

    private void WriteCheckpoint(bool? qualified = null)
    {
        var bytes = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(Snapshot(qualified ?? _qualified)));
        var temporary = Path.Combine(_directory, ".retained-integrity." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            CameraAgentFileSystem.PublishPrepared(_directory, temporary, _checkpoint, overwrite: true);
            RawIngressFileStore.SyncDirectory(_directory);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool OptionalIo(Exception exception)
        => exception is InvalidDataException or IOException or UnauthorizedAccessException or CryptographicException or SqliteException or JsonException or FormatException;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Roots.TryRemove(new KeyValuePair<string, RetainedIntegrityLedger>(_root, this));
        _qualified = false;
        _database.Dispose();
        _ownership.Dispose();
        _barrier.Dispose();
    }

    // Before an integrity owner exists, even an in-flight managed write must
    // finish before root registration/initial enumeration. AsyncLocal supports
    // nested publication adapters without a thread-affine lock across awaits.
    private sealed class RegistrationLease : IDisposable
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly AsyncLocal<RegistrationLease?> Ambient = new();
        private int _depth = 1;
        internal static RegistrationLease Enter(bool registering = false)
        {
            if (Ambient.Value is { } current && Volatile.Read(ref current._depth) > 0)
            {
                if (registering) throw new InvalidOperationException("Initial integrity ownership cannot begin inside an untracked mutation.");
                Interlocked.Increment(ref current._depth);
                return current;
            }
            Gate.Wait();
            var lease = new RegistrationLease();
            Ambient.Value = lease;
            return lease;
        }
        public void Dispose()
        {
            if (Interlocked.Decrement(ref _depth) != 0) return;
            if (Ambient.Value == this) Ambient.Value = null;
            Gate.Release();
        }
    }

    private sealed class Release(SemaphoreSlim barrier) : IDisposable { public void Dispose() => barrier.Release(); }
    private sealed class Nested : IDisposable
    {
        private readonly Mutation _mutation;
        internal Nested(Mutation mutation) { _mutation = mutation; mutation.Nest(); }
        public void Dispose() => _mutation.Dispose();
    }
    private sealed class Multiple(List<IDisposable> scopes) : IDisposable
    {
        public void Dispose() { foreach (var scope in scopes.AsEnumerable().Reverse()) scope.Dispose(); }
    }
    private sealed class Empty : IDisposable
    {
        internal static readonly Empty Instance = new();
        public void Dispose() { }
    }
}
