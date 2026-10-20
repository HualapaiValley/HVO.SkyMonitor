using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>
/// Reads verified orbital snapshots from the <see cref="SqliteOrbitalElementsStore"/> that owns the root. Every
/// materialization proves the stored manifest and payload bytes against their committed identities and through
/// <see cref="OrbitalSnapshot.Parse"/>, and holds a pin so retention cannot retire the snapshot mid-load.
/// </summary>
/// <remarks>
/// <para>
/// <c>configured</c> states whether orbital storage is configured for this CameraAgent at all. It is not refresh
/// enablement: a disabled or halted refresh keeps every retained snapshot readable.
/// </para>
/// <para>
/// At most two materialized snapshots are cached by identity. A cache hit costs no I/O until retention tombstones
/// any snapshot, after which each cached identity is revalidated against the ledger once before it is served again.
/// </para>
/// </remarks>
public sealed partial class OrbitalSnapshotReader : IOrbitalSnapshotReader
{
    internal const int CacheCapacity = 2;
    private const int MaximumCurrentAttempts = 3;

    private readonly SqliteOrbitalElementsStore _store;
    private readonly bool _configured;
    private readonly ILogger<OrbitalSnapshotReader> _logger;
    private readonly object _cacheSync = new();
    private readonly LinkedList<CacheEntry> _cache = new();

    public OrbitalSnapshotReader(SqliteOrbitalElementsStore store, bool configured, ILogger<OrbitalSnapshotReader> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _configured = configured;
        _logger = logger;
    }

    /// <summary>Number of materializations from disk, for tests that prove cache hits perform no I/O.</summary>
    internal int LoadCount => Volatile.Read(ref _loadCount);

    private int _loadCount;

    /// <inheritdoc />
    public async ValueTask<OrbitalSnapshotReadResult> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_configured)
        {
            return Unavailable(OrbitalSnapshotUnavailableReason.NotConfigured);
        }
        try
        {
            await _store.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            for (var attempt = 0; attempt < MaximumCurrentAttempts; attempt++)
            {
                var active = _store.ActiveManifestSha256;
                if (active is null)
                {
                    return Unavailable(OrbitalSnapshotUnavailableReason.NoActiveSnapshot);
                }
                var result = await ReadIdentityAsync(active, cancellationToken).ConfigureAwait(false);
                if (result.UnavailableReason is not (OrbitalSnapshotUnavailableReason.Retired or OrbitalSnapshotUnavailableReason.UnknownIdentity))
                {
                    return result;
                }
                // The active snapshot is never retired; observing that means activation moved under this read.
            }
            return Unavailable(OrbitalSnapshotUnavailableReason.StorageUnavailable);
        }
        catch (Exception exception) when (MapFault(exception, cancellationToken) is { } reason)
        {
            ReadFailed(_logger, reason, exception);
            return Unavailable(reason);
        }
    }

    /// <inheritdoc />
    public async ValueTask<OrbitalSnapshotReadResult> ReadExactAsync(string snapshotSha256, CancellationToken cancellationToken)
    {
        if (!OrbitalContract.IsSha256(snapshotSha256))
        {
            return Unavailable(OrbitalSnapshotUnavailableReason.InvalidIdentity);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!_configured)
        {
            return Unavailable(OrbitalSnapshotUnavailableReason.NotConfigured);
        }
        try
        {
            await _store.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
            return await ReadIdentityAsync(snapshotSha256, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (MapFault(exception, cancellationToken) is { } reason)
        {
            ReadFailed(_logger, reason, exception);
            return Unavailable(reason);
        }
    }

    private async ValueTask<OrbitalSnapshotReadResult> ReadIdentityAsync(string manifestSha256, CancellationToken cancellationToken)
    {
        // Read before pinning: a tombstone committed after this point either sees the pin or advances the generation.
        var generation = _store.RetentionGeneration;
        if (TryGetCached(manifestSha256, generation, refreshGeneration: false) is { } fresh)
        {
            return OrbitalSnapshotReadResult.Available(fresh);
        }
        using var pin = await _store.PinAsync(manifestSha256, cancellationToken).ConfigureAwait(false);
        var row = await _store.FindSnapshotAsync(manifestSha256, cancellationToken).ConfigureAwait(false);
        if (row is null || row.IsRetired)
        {
            Evict(manifestSha256);
            return Unavailable(row is null
                ? OrbitalSnapshotUnavailableReason.UnknownIdentity
                : OrbitalSnapshotUnavailableReason.Retired);
        }
        // Published and pinned: it cannot be retired until the pin is released, so a cached copy is still valid.
        if (TryGetCached(manifestSha256, generation, refreshGeneration: true) is { } revalidated)
        {
            return OrbitalSnapshotReadResult.Available(revalidated);
        }
        using (await _store.AcquireLoadLeaseAsync(cancellationToken).ConfigureAwait(false))
        {
            if (TryGetCached(manifestSha256, generation, refreshGeneration: true) is { } loadedMeanwhile)
            {
                return OrbitalSnapshotReadResult.Available(loadedMeanwhile);
            }
            var snapshot = await LoadAsync(row, cancellationToken).ConfigureAwait(false);
            if (snapshot is null)
            {
                IntegrityFailed(_logger, manifestSha256);
                return Unavailable(OrbitalSnapshotUnavailableReason.IntegrityFailure);
            }
            AddToCache(manifestSha256, snapshot, generation);
            return OrbitalSnapshotReadResult.Available(snapshot);
        }
    }

    /// <summary>Returns null when the stored bytes are missing, the wrong size or do not prove the committed identity.</summary>
    private async ValueTask<OrbitalSnapshot?> LoadAsync(OrbitalStoredSnapshot row, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCount);
        var manifestBytes = await _store.ReadVerifiedFileAsync(_store.ManifestPath(row.ManifestSha256),
            row.ManifestLengthBytes, OrbitalContract.MaximumPayloadBytes, row.ManifestFileSha256, cancellationToken)
            .ConfigureAwait(false);
        if (manifestBytes is null)
        {
            return null;
        }
        var payloadBytes = await _store.ReadVerifiedFileAsync(_store.PayloadPath(row.PayloadSha256),
            row.PayloadLengthBytes, OrbitalContract.MaximumPayloadBytes, row.PayloadSha256, cancellationToken)
            .ConfigureAwait(false);
        if (payloadBytes is null)
        {
            return null;
        }
        var parsed = OrbitalSnapshot.Parse(manifestBytes, payloadBytes);
        return parsed.Snapshot is { } snapshot &&
            string.Equals(snapshot.Manifest.ManifestSha256, row.ManifestSha256, StringComparison.Ordinal) &&
            string.Equals(snapshot.Manifest.NormalizedPayloadSha256, row.PayloadSha256, StringComparison.Ordinal)
                ? snapshot
                : null;
    }

    /// <summary>
    /// Missing or contained-violating bytes for a known manifest are integrity failures; database, I/O, disposal and
    /// fail-closed schema errors are storage unavailability. Cancellation of the caller's token propagates.
    /// </summary>
    private static OrbitalSnapshotUnavailableReason? MapFault(Exception exception, CancellationToken cancellationToken)
        => exception switch
        {
            OperationCanceledException when cancellationToken.IsCancellationRequested => null,
            FileSystemFaultException { Kind: FileSystemFaultKind.Cancelled } when cancellationToken.IsCancellationRequested => null,
            FileSystemFaultException { Kind: FileSystemFaultKind.Containment } => OrbitalSnapshotUnavailableReason.IntegrityFailure,
            FileNotFoundException or DirectoryNotFoundException => OrbitalSnapshotUnavailableReason.IntegrityFailure,
            IOException or UnauthorizedAccessException or SqliteException or InvalidDataException or InvalidOperationException
                or OperationCanceledException => OrbitalSnapshotUnavailableReason.StorageUnavailable,
            _ => null
        };

    private OrbitalSnapshot? TryGetCached(string manifestSha256, long generation, bool refreshGeneration)
    {
        lock (_cacheSync)
        {
            for (var node = _cache.First; node is not null; node = node.Next)
            {
                if (!string.Equals(node.Value.ManifestSha256, manifestSha256, StringComparison.Ordinal))
                {
                    continue;
                }
                if (refreshGeneration)
                {
                    node.Value.Generation = generation;
                }
                else if (node.Value.Generation != generation)
                {
                    return null;
                }
                _cache.Remove(node);
                _cache.AddFirst(node);
                return node.Value.Snapshot;
            }
            return null;
        }
    }

    private void AddToCache(string manifestSha256, OrbitalSnapshot snapshot, long generation)
    {
        lock (_cacheSync)
        {
            _cache.AddFirst(new CacheEntry(manifestSha256, snapshot, generation));
            while (_cache.Count > CacheCapacity)
            {
                _cache.RemoveLast();
            }
        }
    }

    private void Evict(string manifestSha256)
    {
        lock (_cacheSync)
        {
            for (var node = _cache.First; node is not null; node = node.Next)
            {
                if (string.Equals(node.Value.ManifestSha256, manifestSha256, StringComparison.Ordinal))
                {
                    _cache.Remove(node);
                    return;
                }
            }
        }
    }

    internal IReadOnlyList<string> CachedIdentities()
    {
        lock (_cacheSync)
        {
            return _cache.Select(static entry => entry.ManifestSha256).ToArray();
        }
    }

    private static OrbitalSnapshotReadResult Unavailable(OrbitalSnapshotUnavailableReason reason)
        => OrbitalSnapshotReadResult.Unavailable(reason);

    private sealed class CacheEntry(string manifestSha256, OrbitalSnapshot snapshot, long generation)
    {
        public string ManifestSha256 { get; } = manifestSha256;

        public OrbitalSnapshot Snapshot { get; } = snapshot;

        public long Generation { get; set; } = generation;
    }

    // Event IDs 7720-7729 belong to the orbital snapshot reader.
    [LoggerMessage(7720, LogLevel.Warning, "An orbital snapshot read was unavailable ({Reason})")]
    private static partial void ReadFailed(ILogger logger, OrbitalSnapshotUnavailableReason reason, Exception exception);

    [LoggerMessage(7721, LogLevel.Error, "Stored orbital snapshot {ManifestSha256} failed integrity verification")]
    private static partial void IntegrityFailed(ILogger logger, string manifestSha256);
}
