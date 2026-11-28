using static HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation.RetainedIntegrityFormat;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.LocalFiles;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

// Expected content and managed mutation intents, not cached installation identity.
// A process epoch authenticates complete expectations before incremental or full qualification.
internal sealed partial class RetainedIntegrityLedger : IDisposable
{
    internal const string LocationDirectoryName = ".location";
    internal const string DatabaseName = "retained-integrity.v1.db";
    internal const string CheckpointName = "retained-integrity.v1.protected";
    internal const string LockFileName = "retained-integrity.v1.lock";
    internal const string HistoryFileName = "deployment-location.v1.protected";
    internal const string ManualFileName = "manual-deployment-location.v1.protected";
    internal const string IdentityMarkerName = ".deployment-location.v1.identity";
    internal const int BoundedAuthorityEntryLimit = 64;
    private static readonly ConcurrentDictionary<string, RetainedIntegrityLedger> Roots = new(StringComparer.Ordinal);
    private static readonly AsyncLocal<Mutation?> Current = new();
    internal static Action? BeforeNestedMutationForTest { get; set; }
    internal static Action? RegistrationBlockedForTest { get; set; }
    internal static Action<string>? BeforePendingDirectorySyncForTest { get; set; }
    internal static Action<string>? BeforeAuthorityScanEntryForTest { get; set; }
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
    private readonly bool _recoveredJournal;
    private readonly List<Intent> _pending = [];
    private string _nonce = Guid.NewGuid().ToString("N");
    private byte[] _binding = [];
    private byte[]? _entryKey;
    private byte[] _chain = new byte[64];
    private byte[] _pendingCommitment = new byte[64];
    private long _generation;
    private long _count;
    private bool _established;
    private bool _qualified;
    private bool _warned;
    private bool _pendingLoaded;
    private bool _startupIncremental;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Non-owning reference; the serialized full-verification caller disposes its scrub, and host shutdown drains it before disposing the ledger.")]
    private Scrub? _scrubbing;
    private bool _disposed;

    internal readonly record struct FileStamp(long Length, long ModifiedUtcTicks);
    internal sealed record Entry(string Key, byte[] Digest, int Kind, long Generation = 0)
    {
        internal FileStamp? Stamp { get; set; }
        internal bool Observed { get; set; }
    }
    private sealed record Intent(string Key, bool Tree, long Generation, string? Planned = null, string? Source = null, bool Denied = false,
        string? ReclaimedStagingSha256 = null);
    private sealed record Checkpoint(string Epoch, string Nonce, string Binding, long Generation, string Chain, long Count, bool Qualified,
        DateTimeOffset VerifiedUntilUtc = default, bool CleanShutdown = false);

    internal RetainedIntegrityLedger(string root, IDeploymentLocationProtector protector,
        Func<string, Entry?> readCurrent, Action unqualifiedWarning, TimeProvider timeProvider, string? requiredNonce = null)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        _directory = Path.Combine(_root, LocationDirectoryName);
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
        var ownershipPath = Path.Combine(_directory, LockFileName);
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, ownershipPath);
        try { _ownership = new FileStream(ownershipPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception)
        {
            _database.Dispose();
            throw new InvalidOperationException("The retained root already has a live integrity owner.", exception);
        }
        try
        {
            var databasePath = Path.Combine(_directory, DatabaseName);
            if (requiredNonce is not null && (!File.Exists(databasePath) || new FileInfo(databasePath).Length == 0))
                throw new InvalidDataException("Protected history requires its original retained integrity ledger.");
            _recoveredJournal = new FileInfo(databasePath + "-journal") is { Exists: true, Length: > 0 } ||
                new FileInfo(databasePath + "-wal") is { Exists: true, Length: > 0 };
            _database.Open();
            OpenSchema();
            var metadataExists = ReadMetadata();
            if (requiredNonce is not null && (!metadataExists || !string.Equals(requiredNonce, _nonce, StringComparison.Ordinal)))
                throw new InvalidDataException("Protected history requires its original retained integrity ledger.");
            _pendingLoaded = _pendingCommitment.All(static value => value == 0);
            if (!metadataExists)
            {
                _entryKey = RandomNumberGenerator.GetBytes(32);
                // Bind fresh authority only after its nonce is durable. A crash during
                // adoption can then resume a full scrub, never manufacture another ledger.
                using var transaction = _database.BeginTransaction();
                WriteMetadata(transaction);
                transaction.Commit();
                RawIngressFileStore.SyncDirectory(_directory);
            }
            if (!Roots.TryAdd(_root, this))
                throw new InvalidOperationException("The retained root already has an integrity owner in this process.");
        }
        catch { _database.Dispose(); _ownership.Dispose(); throw; }
        // PrepareStartup authenticates the previous checkpoint; it cannot grant
        // readiness until this process authenticates rows and checks current metadata.
    }

    internal string Nonce => _nonce;
    internal bool RequiresScrub => !Volatile.Read(ref _qualified);
    internal void RequireFullVerification() => Invalidate();
    internal static string FileKey(string root, string path) => "F|" + Path.GetRelativePath(root, path);
    internal static string JournalKey(string capture) => "J|" + capture;

    internal async ValueTask<bool> VerifyAsync(byte[] binding, CancellationToken cancellationToken, bool waitForMutation = false)
    {
        if (waitForMutation) await _barrier.WaitAsync(cancellationToken).ConfigureAwait(false);
        else if (!await _barrier.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false)) return false;
        try
        {
            if (!_qualified) return false;
            if (_timeProvider.GetUtcNow() >= _scrubDue) return Invalidate();
            if (!_binding.AsSpan().SequenceEqual(binding)) return Invalidate();
            ValidatePathMetadata();
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
        _startupIncremental = false;
        if (!_warned) { _warned = true; _unqualifiedWarning(); }
        return false;
    }

    internal Scrub BeginScrub(byte[] binding, Func<string, Entry?> readForScrub, bool strict,
        CancellationToken cancellationToken, bool allowIncremental = false)
    {
        _barrier.Wait(cancellationToken);
        try
        {
            ValidateLiveMetadata();
            ValidatePathMetadata();
            ReadIntents();
            var expected = ReadExpectedEntries(cancellationToken);
            var incremental = allowIncremental && _startupIncremental && _pending.Count == 0 &&
                _binding.AsSpan().SequenceEqual(binding) && _timeProvider.GetUtcNow() < _scrubDue;
            _startupIncremental = false;
            if (_entryKey is null)
            {
                _entryKey = RandomNumberGenerator.GetBytes(32);
                using var transaction = _database.BeginTransaction();
                WriteMetadata(transaction);
                transaction.Commit();
            }
            Execute("DELETE FROM scan_entries;");
            if (incremental) Execute("INSERT INTO scan_entries SELECT * FROM entries;");
            return _scrubbing = new Scrub(this, binding, readForScrub, _generation, strict, expected, incremental);
        }
        catch (Exception exception) when (exception is not OperationCanceledException) { Invalidate(); throw; }
        finally { _barrier.Release(); }
    }

    internal sealed class Scrub(RetainedIntegrityLedger owner, byte[] binding,
        Func<string, Entry?> readCurrent, long startGeneration, bool strict,
        Dictionary<string, Entry> expectedEntries, bool incremental) : IDisposable
    {
        private bool _finished;
        private bool _locked;
        private bool _invalid;
        private bool _drift;
        private bool _disposed;
        private readonly List<Entry> _batch = [];
        private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
        private readonly byte[] _scanChain = incremental ? owner._chain.ToArray() : new byte[64];
        internal bool Incremental => incremental;
        internal void LoadFileMetadata(RetainedManifestValidationIndex index, CancellationToken cancellationToken)
        {
            if (incremental) index.LoadFileMetadata(expectedEntries, cancellationToken);
        }
        internal Entry? Reuse(string key, FileStamp? stamp = null, byte[]? digest = null, long? length = null)
        {
            if (!incremental || _changed.Contains(key) || !expectedEntries.TryGetValue(key, out var entry) ||
                entry.Kind is not (1 or 2)) return null;
            if (stamp is not null ? entry.Stamp != stamp :
                digest is null || entry.Stamp is not { ModifiedUtcTicks: 0 } journal || journal.Length != length ||
                !entry.Digest.AsSpan().SequenceEqual(digest)) return null;
            entry.Observed = true;
            return entry;
        }
        private readonly bool _priorQualified = owner._qualified && owner._timeProvider.GetUtcNow() < owner._scrubDue;
        internal void Fail() => owner.Invalidate();
        internal void Changed(string key) => _changed.Add(key);
        internal int? ParsedKind(string key, byte[] digest)
        {
            if (!binding.AsSpan().SequenceEqual(owner._binding)) return null;
            var expected = _changed.Contains(key) ? owner.ReadEntry(key) : expectedEntries.GetValueOrDefault(key);
            return expected is { Kind: 1 or 2 } && expected.Digest.AsSpan().SequenceEqual(digest) ? expected.Kind : null;
        }
        private void Flush()
        {
            if (_batch.Count == 0) return;
            using var transaction = owner._database.BeginTransaction();
            owner.UpsertBatch(_batch, transaction);
            transaction.Commit();
            _batch.Clear();
        }
        internal IDisposable Observe(CancellationToken cancellationToken)
        {
            owner._barrier.Wait(cancellationToken);
            return new Release(owner._barrier);
        }

        // Called while Observe holds the short per-entry mutation barrier.
        internal void Record(Entry entry, bool replace = false, RetainedManifestValidationIndex? index = null)
        {
            if (expectedEntries.TryGetValue(entry.Key, out var original)) original.Observed = true;
            var expected = _changed.Contains(entry.Key) ? owner.ReadEntry(entry.Key) : expectedEntries.GetValueOrDefault(entry.Key);
            if (owner._established && !owner.AllowedPending(entry.Key, entry.Digest) &&
                (expected is null || expected.Kind == 0 || !expected.Digest.AsSpan().SequenceEqual(entry.Digest)))
                Drift();
            _invalid |= entry.Kind == 3;
            if (entry.Kind == 3) owner.Invalidate();
            entry = entry with { Generation = expected?.Generation ?? startGeneration };
            if (entry.Kind is 1 or 2) index?.RecordFileMetadata(entry);
            if ((replace || incremental) && owner.ReadStageEntry(entry.Key) is { } previous)
                Accumulate(_scanChain, owner.EntryHash(previous), remove: true);
            Accumulate(_scanChain, owner.EntryHash(entry));
            _batch.Add(entry);
            if (_batch.Count >= 8192) Flush();
        }

        private void Drift()
        {
            owner.Invalidate();
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
            var changed = new List<string>();
            if (incremental)
                changed.AddRange(expectedEntries.Values.Where(entry => !entry.Observed && entry.Kind != 0).Select(entry => entry.Key));
            if (!(_changed.Count == 0 && owner._pending.Count == 0 && expectedEntries.Count == owner._count &&
                _scanChain.AsSpan().SequenceEqual(owner._chain)))
            {
                using var query = owner._database.CreateCommand();
                query.CommandText = "SELECT key FROM entries WHERE generation>$start UNION SELECT key FROM entries WHERE kind<>0 AND key NOT IN (SELECT key FROM scan_entries);";
                query.Parameters.AddWithValue("$start", startGeneration);
                using var reader = query.ExecuteReader();
                while (reader.Read()) changed.Add(reader.GetString(0));
            }
            foreach (var key in changed.Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expected = owner.ReadEntry(key);
                var actual = readCurrent(key);
                if (actual is null)
                {
                    if (expected is { Kind: not 0 } && !owner.AllowedPending(key, null))
                        Drift();
                    if (owner.ReadStageEntry(key) is { } previous)
                        Accumulate(_scanChain, owner.EntryHash(previous), remove: true);
                    owner.DeleteStage(key);
                }
                else
                {
                    Record(actual, replace: true);
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
            owner.SyncPendingIntents();
            using var transaction = owner._database.BeginTransaction();
            var verified = owner.ComputeEntriesRoot("scan_entries", transaction);
            if (!verified.AsSpan().SequenceEqual(_scanChain))
                throw new InvalidDataException("Retained verification staging authentication failed.");
            owner.Execute("DELETE FROM entries; INSERT INTO entries SELECT * FROM scan_entries; DELETE FROM scan_entries; DELETE FROM pending;", transaction);
            owner._generation++;
            owner._binding = binding;
            owner._count = owner.Scalar("SELECT COUNT(*) FROM entries WHERE kind<>0;", transaction);
            owner._chain = verified;
            owner._established = true;
            owner.WriteMetadata(transaction);
            transaction.Commit();
            owner._pending.Clear();
            owner.ValidatePathMetadata();
            owner._qualified = !_invalid;
            if (owner._qualified) owner._warned = false;
            if (!incremental) owner._scrubDue = owner._timeProvider.GetUtcNow().AddHours(24);
            owner.WriteCheckpoint();
            _finished = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_finished && (!_priorQualified || _invalid || _drift)) owner._qualified = false;
            if (owner._scrubbing == this) owner._scrubbing = null;
            if (_locked) { _locked = false; owner._barrier.Release(); }
        }
    }

    internal static IDisposable BeginPublication(string source, string destination)
        => BeginPublication(source, destination, Tracked(destination) ? File.ReadAllBytes(source) : []);

    internal static IDisposable BeginPublication(string source, string destination, ReadOnlyMemory<byte> bytes)
    {
        var plans = new List<(string Path, bool Tree, string? Planned, string? Source)>();
        if (Tracked(source)) plans.Add((source, false, null, null));
        if (Tracked(destination)) plans.Add((destination, false, Convert.ToHexString(SHA256.HashData(bytes.Span)), null));
        return plans.Count == 0 ? Empty.Instance : Begin(plans);
    }

    internal static IDisposable? TryBeginPublication(string source, string destination)
        => TryBeginPublication(source, destination, Tracked(destination) ? File.ReadAllBytes(source) : []);

    internal static IDisposable? TryBeginPublication(string source, string destination, ReadOnlyMemory<byte> bytes)
    {
        var plans = new List<(string Path, bool Tree, string? Planned, string? Source)>();
        if (Tracked(source)) plans.Add((source, false, null, null));
        if (Tracked(destination)) plans.Add((destination, false, Convert.ToHexString(SHA256.HashData(bytes.Span)), null));
        return plans.Count == 0 ? Empty.Instance : TryBegin(plans);
    }

    internal static IDisposable BeginWrite(string path, ReadOnlyMemory<byte> bytes)
        => Tracked(path) ? Begin([(path, false, Convert.ToHexString(SHA256.HashData(bytes.Span)), null)]) : Empty.Instance;
    internal static IDisposable BeginDelete(string path) => Tracked(path) ? Begin([(path, false, null, null)]) : Empty.Instance;

    internal static IDisposable? TryBeginDelete(string path)
        => Tracked(path) ? TryBegin([(path, false, null, null)]) : Empty.Instance;
    internal static IDisposable BeginSiloStagingDelete(string sidecarPath, string manifestSha256) =>
        Begin([(sidecarPath, false, null, null)], reclaimedStagingSha256: manifestSha256);
    internal static IDisposable BeginTrees(params string[] paths) => Begin(paths.Select(path => (path, true, (string?)null, (string?)null)));
    internal static IDisposable BeginJournalMetadataWrite() => RegistrationLease.Enter();
    internal static IDisposable BeginTreeMove(string source, string destination)
        => Begin([(source, true, null, null), (destination, true, null, Path.GetFullPath(source))]);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned composite owns the registration lease and mutation; it disposes both in reverse order, including on a disposal failure.")]
    internal static IDisposable BeginJournal(string databasePath, string? capture = null, byte[]? manifest = null)
    {
        if (Current.Value is { Active: true }) BeforeNestedMutationForTest?.Invoke();
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(databasePath)))!;
        var registration = RegistrationLease.Enter();
        try
        {
            if (!Roots.TryGetValue(root, out var owner)) return registration;
            var scope = owner.Enter([new Intent(capture is null ? "J|" : JournalKey(capture), capture is null, 0,
                manifest is null ? null : Convert.ToHexString(SHA256.HashData(manifest)))]);
            return new Multiple([registration, scope]);
        }
        catch { registration.Dispose(); throw; }
    }
    private static bool Tracked(string path) => path.EndsWith(".json", StringComparison.Ordinal);

    private static Multiple Begin(IEnumerable<(string Path, bool Tree, string? Planned, string? Source)> plans,
        string? reclaimedStagingSha256 = null)
        => TryBeginCore(plans, requireAvailableLedger: false, reclaimedStagingSha256)!;

    private static Multiple? TryBegin(IEnumerable<(string Path, bool Tree, string? Planned, string? Source)> plans,
        string? reclaimedStagingSha256 = null)
        => TryBeginCore(plans, requireAvailableLedger: true, reclaimedStagingSha256);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned composite owns the registration lease and root scopes and disposes them in reverse order.")]
    private static Multiple? TryBeginCore(
        IEnumerable<(string Path, bool Tree, string? Planned, string? Source)> plans,
        bool requireAvailableLedger,
        string? reclaimedStagingSha256 = null)
    {
        if (Current.Value is { Active: true }) BeforeNestedMutationForTest?.Invoke();
        var registration = RegistrationLease.Enter();
        var grouped = new Dictionary<RetainedIntegrityLedger, List<Intent>>();
        try
        {
            foreach (var plan in plans)
            {
                var full = Path.GetFullPath(plan.Path);
                var current = full;
                while (current is not null && !Roots.ContainsKey(current)) current = Path.GetDirectoryName(current);
                if (current is null || !Roots.TryGetValue(current, out var owner))
                {
                    if (requireAvailableLedger && (plan.Tree || Tracked(full)) && HasEstablishedLedgerOnDisk(full))
                    {
                        registration.Dispose();
                        return null;
                    }
                    continue;
                }
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
                    plan.Tree, 0, plan.Planned, sourceKey, ReclaimedStagingSha256: reclaimedStagingSha256);
                if (!grouped.TryGetValue(owner, out var list)) grouped[owner] = list = [];
                list.Add(intent);
            }
        }
        catch { registration.Dispose(); throw; }
        if (grouped.Count == 0)
        {
            return new Multiple([registration]);
        }
        // Retain the ambient registration lease until every root scope completes.
        // Releasing it while holding a root barrier lets another writer own the
        // global gate while waiting for that root, deadlocking a nested write.
        var scopes = new List<IDisposable> { registration };
        try
        {
            foreach (var (owner, intents) in grouped.OrderBy(pair => pair.Key._root, StringComparer.Ordinal)) scopes.Add(owner.Enter(intents));
            return new Multiple(scopes);
        }
        catch (Exception exception) when (requireAvailableLedger && OptionalIo(exception))
        {
            new Multiple(scopes).Dispose();
            return null;
        }
        catch
        {
            new Multiple(scopes).Dispose();
            throw;
        }
    }

    private static readonly string[] EstablishedLedgerMarkerNames =
    [
        DatabaseName,
        DatabaseName + "-journal",
        DatabaseName + "-wal",
        DatabaseName + "-shm",
        CheckpointName,
        LockFileName,
        HistoryFileName,
        ManualFileName
    ];

    private static bool IsEstablishedLedgerMarkerName(string name)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var marker in EstablishedLedgerMarkerNames)
        {
            if (string.Equals(name, marker, comparison))
            {
                return true;
            }
        }
        return false;
    }

    internal static bool PathExistsOrInaccessible(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    internal static bool HasEstablishedAuthorityAtRoot(string rootDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Detect the durable root-level deployment-location identity marker even if .location
        // is offline, missing, or recreated empty during recovery startup.
        if (PathExistsOrInaccessible(Path.Combine(rootDirectory, IdentityMarkerName)))
        {
            return true;
        }

        var locationDirectory = Path.Combine(rootDirectory, LocationDirectoryName);
        FileAttributes locationAttributes;
        try
        {
            locationAttributes = File.GetAttributes(locationDirectory);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return true;
        }

        if ((locationAttributes & FileAttributes.ReparsePoint) != 0)
        {
            return true;
        }

        if ((locationAttributes & FileAttributes.Directory) == 0)
        {
            return false;
        }

        foreach (var marker in EstablishedLedgerMarkerNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PathExistsOrInaccessible(Path.Combine(locationDirectory, marker)))
            {
                return true;
            }
        }

        try
        {
            var count = 0;
            foreach (var entry in Directory.EnumerateFileSystemEntries(locationDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > BoundedAuthorityEntryLimit)
                {
                    return true;
                }
                BeforeAuthorityScanEntryForTest?.Invoke(entry);
                if (IsEstablishedLedgerMarkerName(Path.GetFileName(entry)))
                {
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return true;
        }

        return false;
    }

    private static bool HasEstablishedLedgerOnDisk(string fullPath)
    {
        var current = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(current))
        {
            if (HasEstablishedAuthorityAtRoot(current))
            {
                return true;
            }
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }
            current = parent;
        }
        return false;
    }

    private IDisposable Enter(IEnumerable<Intent> intents)
    {
        var current = Current.Value;
        if (current?.Find(this) is { } existing)
        {
            existing.Add(intents);
            return new Nested(existing);
        }
        _barrier.Wait();
        var mutation = new Mutation(this, current);
        try
        {
            if (!_pendingLoaded) ReadIntents();
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

    internal static void AbandonCurrentMutation() => Current.Value?.Abandon();

    private sealed class Mutation(RetainedIntegrityLedger owner, Mutation? parent) : IDisposable
    {
        private readonly Dictionary<string, Intent> _intents = new(StringComparer.Ordinal);
        private int _depth = 1;
        private bool _abandoned;
        internal RetainedIntegrityLedger Owner => owner;
        internal bool Active => _depth > 0;
        internal void Abandon()
        {
            _abandoned = true;
            owner.Invalidate();
        }
        internal Mutation? Find(RetainedIntegrityLedger candidate)
            => Active && Owner == candidate ? this : parent?.Find(candidate);
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
                if (_abandoned)
                {
                    owner.Invalidate();
                }
                else
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
        _scrubbing?.Changed(entry.Key);
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

    // Reconciling an interrupted managed mutation commits the observed filesystem outcome and clears
    // its pending intent. Durably sync any present target file and every existing parent/ancestor
    // directory first so power loss after clearing the pending row cannot revert directory entries.
    private void SyncPendingIntents()
    {
        if (_pending.Count == 0) return;
        var directories = new List<string>();
        var seenDirectories = new HashSet<string>(StringComparer.Ordinal);
        var syncedFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var intent in _pending)
        {
            CollectPendingSyncTargets(intent, syncedFiles, directories, seenDirectories);
        }
        directories.Sort((left, right) =>
        {
            var depthComparison = RelativeDirectoryDepth(right).CompareTo(RelativeDirectoryDepth(left));
            return depthComparison != 0 ? depthComparison : StringComparer.Ordinal.Compare(left, right);
        });
        foreach (var directory in directories)
        {
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, directory);
            BeforePendingDirectorySyncForTest?.Invoke(directory);
            DurableSync.Directory(directory);
        }
    }

    private int RelativeDirectoryDepth(string normalizedPath)
    {
        if (IsRootDirectory(normalizedPath)) return 0;
        var depth = 0;
        for (var index = _root.Length; index < normalizedPath.Length; index++)
        {
            var ch = normalizedPath[index];
            if (ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar)
            {
                depth++;
            }
        }
        return depth;
    }

    private void CollectPendingSyncTargets(
        Intent intent,
        HashSet<string> syncedFiles,
        List<string> directories,
        HashSet<string> seenDirectories)
    {
        if (intent.Key.StartsWith("J|", StringComparison.Ordinal))
        {
            var journalDirectory = Path.Combine(_root, "journal");
            var journalDatabase = Path.Combine(journalDirectory, "raw-ingress.db");
            SyncPendingFileIfPresent(journalDatabase, syncedFiles);
            AddPendingDirectoryHierarchy(journalDirectory, directories, seenDirectories);
            return;
        }
        if (!intent.Key.StartsWith("F|", StringComparison.Ordinal)) return;
        if (intent.Tree)
        {
            var treeDirectory = Path.Combine(_root, intent.Key[2..].TrimEnd(Path.DirectorySeparatorChar));
            CollectPendingTreeSyncTargets(treeDirectory, syncedFiles, directories, seenDirectories);
            if (intent.Source is not null && intent.Source.StartsWith("F|", StringComparison.Ordinal))
            {
                var sourceDirectory = Path.Combine(_root, intent.Source[2..].TrimEnd(Path.DirectorySeparatorChar));
                var normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory));
                if (!IsRootDirectory(normalizedSource))
                    AddPendingDirectoryHierarchy(Path.GetDirectoryName(normalizedSource), directories, seenDirectories);
            }
            return;
        }
        var path = Path.Combine(_root, intent.Key[2..]);
        SyncPendingFileIfPresent(path, syncedFiles);
        AddPendingDirectoryHierarchy(Path.GetDirectoryName(path), directories, seenDirectories);
    }

    private void CollectPendingTreeSyncTargets(
        string treeDirectory,
        HashSet<string> syncedFiles,
        List<string> directories,
        HashSet<string> seenDirectories)
    {
        var fullTree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(treeDirectory));
        if (!IsRootOrDescendant(fullTree))
            throw new InvalidDataException("Pending integrity tree path escaped the retained root.");
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, fullTree);
        if (Directory.Exists(fullTree))
        {
            var subdirectories = new List<string>();
            var stack = new Stack<string>();
            stack.Push(fullTree);
            while (stack.TryPop(out var current))
            {
                subdirectories.Add(current);
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    RawIngressFileStore.EnsureNoSymbolicLinks(_root, entry);
                    if (Directory.Exists(entry))
                    {
                        stack.Push(Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry)));
                    }
                    else if (Tracked(entry))
                    {
                        SyncPendingFileIfPresent(entry, syncedFiles);
                    }
                }
            }
            for (var index = subdirectories.Count - 1; index >= 0; index--)
            {
                if (seenDirectories.Add(subdirectories[index]))
                    directories.Add(subdirectories[index]);
            }
        }
        if (!IsRootDirectory(fullTree))
            AddPendingDirectoryHierarchy(Path.GetDirectoryName(fullTree), directories, seenDirectories);
    }

    private void SyncPendingFileIfPresent(string path, HashSet<string> syncedFiles)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsRootOrDescendant(fullPath))
            throw new InvalidDataException("Pending integrity path escaped the retained root.");
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, fullPath);
        if (!File.Exists(fullPath) || !syncedFiles.Add(fullPath)) return;
        DurableSync.File(fullPath);
    }

    private void AddPendingDirectoryHierarchy(
        string? directory,
        List<string> directories,
        HashSet<string> seenDirectories)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!IsRootOrDescendant(current))
            throw new InvalidDataException("Pending integrity directory escaped the retained root.");
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, current);
        while (IsRootOrDescendant(current))
        {
            if (Directory.Exists(current) && seenDirectories.Add(current))
                directories.Add(current);
            if (IsRootDirectory(current)) break;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent)) break;
            current = Path.TrimEndingDirectorySeparator(parent);
        }
    }

    private bool IsRootDirectory(string normalizedPath)
        => string.Equals(normalizedPath, _root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private bool IsRootOrDescendant(string fullPath)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(normalized, _root, comparison) ||
            normalized.StartsWith(_root + Path.DirectorySeparatorChar, comparison);
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
        // A qualified Silo publication or write-off owns this local staging deletion. The supplied
        // digest must match any retained expectation before we can discard missing or damaged staging.
        if (intent.ReclaimedStagingSha256 is { } reclaimed &&
            (ReadEntry(intent.Key) is not { Kind: not 0 } staging ||
                string.Equals(reclaimed, Convert.ToHexString(staging.Digest), StringComparison.Ordinal)))
            return false;
        var current = _readCurrent(intent.Key);
        var expected = ReadEntry(intent.Key);
        // A nested prune may delete the file just published by its still-open
        // outer write. Its declared, authenticated intent is the expected state.
        if (current is not null && AllowedPending(intent.Key, current.Digest)) return false;
        var changed = current is null ? expected is { Kind: not 0 }
            : expected is null or { Kind: 0 } || !expected.Digest.AsSpan().SequenceEqual(current.Digest);
        if (changed && expected is not null && intent.Planned == Convert.ToHexString(expected.Digest)) return false;
        if (changed) Invalidate();
        return changed;
    }
    private static string PrefixEnd(string prefix) => prefix[..^1] + (char)(prefix[^1] + 1);
    private Checkpoint Snapshot(bool qualified, bool cleanShutdown = false) => new(_epoch, _nonce, Convert.ToHexString(_binding),
        _generation, Convert.ToHexString(_chain), _count, qualified, _scrubDue, cleanShutdown);

    private void WriteCheckpoint(bool? qualified = null, bool cleanShutdown = false)
    {
        var bytes = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(Snapshot(qualified ?? _qualified, cleanShutdown)));
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
        if (_barrier.Wait(0))
        {
            try
            {
                if (_qualified && _pending.Count == 0 && _scrubbing is null)
                {
                    ValidateLiveMetadata();
                    ValidatePathMetadata();
                    WriteCheckpoint(cleanShutdown: true);
                }
            }
            catch (Exception exception) when (OptionalIo(exception)) { Invalidate(); }
            finally { _barrier.Release(); }
        }
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
            if (!Gate.Wait(0))
            {
                RegistrationBlockedForTest?.Invoke();
                Gate.Wait();
            }
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
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            ReleaseAt(scopes.Count - 1);
        }
        private void ReleaseAt(int index)
        {
            if (index < 0) return;
            try { scopes[index].Dispose(); }
            finally { ReleaseAt(index - 1); }
        }
    }
    private sealed class Empty : IDisposable
    {
        internal static readonly Empty Instance = new();
        public void Dispose() { }
    }
}
