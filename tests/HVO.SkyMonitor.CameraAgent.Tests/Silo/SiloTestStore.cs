using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// A generated local versioned bucket behind the narrow store seam. It keeps every version's exact bytes and stored
/// metadata separately, so a test can delete a version, damage bytes behind intact metadata, lose an acknowledged
/// version, or make a key time out, exactly as the fault matrix requires.
/// </summary>
internal sealed class SiloTestStore(string storeId, string bucket, SiloStoreClass storeClass = SiloStoreClass.Local, bool qualified = true)
    : ISiloObjectStore
{
    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, List<StoredVersion>> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _served = new(StringComparer.Ordinal);
    private readonly HashSet<SiloExactLocator> _unavailableVersions = [];
    private int _counter;

    private sealed record StoredVersion(string VersionId, byte[] Bytes, IReadOnlyDictionary<string, string> Metadata);

    public SiloStoreDescriptor Descriptor { get; } = new(storeId, bucket, storeClass, SiloRetrieval.Online, qualified);

    public long BytesStored
    {
        get
        {
            lock (_gate)
                return _objects.Values.SelectMany(static versions => versions).Sum(static version => version.Bytes.LongLength);
        }
    }

    /// <summary>How many bytes of the key's versions readers have consumed.</summary>
    public long BytesServed(string key)
    {
        lock (_gate)
        {
            return _served.GetValueOrDefault(key);
        }
    }

    private void Served(string key, int read)
    {
        lock (_gate)
        {
            _served[key] = _served.GetValueOrDefault(key) + read;
        }
    }

    public bool Unreachable { get; set; }

    /// <summary>The PUT never lands and the client sees a timeout.</summary>
    public Func<string, bool>? FailBeforeLanding { get; set; }

    /// <summary>The PUT lands and the client sees a timeout: an uncertain write.</summary>
    public Func<string, bool>? FailAfterLanding { get; set; }

    /// <summary>The PUT is acknowledged but the version is gone before readback, as an unqualified power cut would do.</summary>
    public Func<string, bool>? LoseAfterLanding { get; set; }

    /// <summary>A GET of the key times out.</summary>
    public Func<string, bool>? ReadUnavailable { get; set; }

    /// <summary>The process dies while the PUT is in flight, before anything lands.</summary>
    public Func<string, bool>? CrashBeforeLanding { get; set; }

    /// <summary>Runs after a PUT lands, before the writer reads it back: another actor acting on the bucket meanwhile.</summary>
    public Action<string>? AfterLanding { get; set; }

    /// <summary>An exact-version GET of the key misses although the version exists, as a lagging read can.</summary>
    public Func<string, bool>? MissOnRead { get; set; }

    /// <summary>Replaces the body returned for an existing version.</summary>
    public Func<SiloExactLocator, Stream?>? ReadContentOverride { get; set; }

    /// <summary>A HEAD for the key's current version times out: a read failure that says nothing about any write.</summary>
    public Func<string, bool>? HeadUnavailable { get; set; }

    public Func<CancellationToken, Task>? BeforeList { get; set; }

    public int Puts { get; private set; }

    public async ValueTask<SiloPutResult> PutCreateOnlyAsync(SiloPutRequest request, CancellationToken cancellationToken)
    {
        ThrowIfUnreachable("put");
        if (FailBeforeLanding?.Invoke(request.Key) == true)
        {
            throw new SiloStoreUnavailableException(Descriptor.StoreId, "put " + request.Key, writeMayHaveLanded: false);
        }
        if (CrashBeforeLanding?.Invoke(request.Key) == true)
        {
            throw new InvalidOperationException("simulated crash while putting " + request.Key);
        }
        var stream = await request.OpenContent(cancellationToken).ConfigureAwait(false);
        byte[] bytes;
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }
        if (bytes.Length != request.Length || !string.Equals(SiloHash.Sha256(bytes), request.Sha256, StringComparison.Ordinal))
        {
            throw new SiloChecksumRejectedException(request.Key);
        }
        string version;
        lock (_gate)
        {
            Puts++;
            if (_objects.TryGetValue(request.Key, out var existing) && existing.Count > 0)
            {
                return new(false, null);
            }
            version = NextVersionLocked();
            Versions(request.Key).Add(new(version, bytes, new Dictionary<string, string>(request.Metadata)));
            if (LoseAfterLanding?.Invoke(request.Key) == true)
            {
                Versions(request.Key).RemoveAll(stored => stored.VersionId == version);
            }
        }
        AfterLanding?.Invoke(request.Key);
        if (FailAfterLanding?.Invoke(request.Key) == true)
        {
            throw new SiloStoreUnavailableException(Descriptor.StoreId, "put " + request.Key, writeMayHaveLanded: true);
        }
        return new(true, version);
    }

    public ValueTask<SiloOpenResult> OpenVersionAsync(string key, string versionId, CancellationToken cancellationToken)
    {
        ThrowIfUnreachable("get");
        if (ReadUnavailable?.Invoke(key) == true)
        {
            throw new SiloStoreUnavailableException(Descriptor.StoreId, "get " + key, writeMayHaveLanded: false);
        }
        if (MissOnRead?.Invoke(key) == true)
        {
            return ValueTask.FromResult(new SiloOpenResult(SiloOpenStatus.NoSuchVersion, null));
        }
        lock (_gate)
        {
            if (_unavailableVersions.Contains(Locator(key, versionId)))
                throw new SiloStoreUnavailableException(Descriptor.StoreId, "get " + key, writeMayHaveLanded: false);
            if (!_objects.TryGetValue(key, out var versions) || versions.Count == 0)
            {
                return ValueTask.FromResult(new SiloOpenResult(SiloOpenStatus.NoSuchKey, null));
            }
            var found = versions.FirstOrDefault(stored => stored.VersionId == versionId);
            return ValueTask.FromResult(found is null
                ? new SiloOpenResult(SiloOpenStatus.NoSuchVersion, null)
                : new SiloOpenResult(SiloOpenStatus.Found, ReadContentOverride?.Invoke(Locator(key, versionId))
                    ?? new ServedStream(found.Bytes.ToArray(), read => Served(key, read))));
        }
    }

    public ValueTask<string?> LocateCurrentVersionAsync(string key, CancellationToken cancellationToken)
    {
        ThrowIfUnreachable("head");
        if (HeadUnavailable?.Invoke(key) == true)
        {
            throw new SiloStoreUnavailableException(Descriptor.StoreId, "head " + key, writeMayHaveLanded: false);
        }
        lock (_gate)
        {
            return ValueTask.FromResult(_objects.TryGetValue(key, out var versions) && versions.Count > 0 ? versions[^1].VersionId : null);
        }
    }

    public async IAsyncEnumerable<string> ListKeysAsync(
        string prefix, string? startAfter, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfUnreachable("list");
        if (BeforeList is { } beforeList)
        {
            await beforeList(cancellationToken).ConfigureAwait(false);
        }
        string[] keys;
        lock (_gate)
        {
            keys = _objects.Where(pair => pair.Value.Count > 0 && pair.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                    (startAfter is null || string.CompareOrdinal(pair.Key, startAfter) > 0))
                .Select(static pair => pair.Key).ToArray();
        }
        foreach (var key in keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return key;
        }
    }

    // ----- Fixture controls ------------------------------------------------------------------------------------

    /// <summary>Create a version directly, bypassing create-only: a replica, an import, or a foreign writer.</summary>
    public string Plant(string key, byte[] bytes, IReadOnlyDictionary<string, string>? metadata = null)
    {
        lock (_gate)
        {
            var version = NextVersionLocked();
            Versions(key).Add(new(version, bytes.ToArray(), metadata ?? new Dictionary<string, string>
            {
                [SiloObjectHeaders.Sha256] = SiloHash.Sha256(bytes)
            }));
            return version;
        }
    }

    public SiloExactLocator Locator(string key, string version) => new(Descriptor.StoreId, Descriptor.Bucket, key, version);

    public string? Current(string key)
    {
        lock (_gate)
        {
            return _objects.TryGetValue(key, out var versions) && versions.Count > 0 ? versions[^1].VersionId : null;
        }
    }

    public IReadOnlyList<string> VersionsOf(string key)
    {
        lock (_gate)
        {
            return _objects.TryGetValue(key, out var versions) ? versions.Select(static stored => stored.VersionId).ToArray() : [];
        }
    }

    public byte[] Read(string key, string version)
    {
        lock (_gate)
        {
            return _objects[key].Single(stored => stored.VersionId == version).Bytes.ToArray();
        }
    }

    public IReadOnlyDictionary<string, string> Metadata(string key, string version)
    {
        lock (_gate)
        {
            return _objects[key].Single(stored => stored.VersionId == version).Metadata;
        }
    }

    /// <summary>A permanent, version-ID delete: the version reads back as NoSuchVersion.</summary>
    public void Delete(string key, string version)
    {
        lock (_gate)
        {
            if (Versions(key).RemoveAll(stored => stored.VersionId == version) != 1)
            {
                throw new InvalidOperationException($"No version {version} at {key}.");
            }
        }
    }

    public void Delete(SiloExactLocator locator) => Delete(locator.Key, locator.VersionId);

    /// <summary>Flip one byte of the stored bytes while the stored checksum metadata stays intact.</summary>
    public void Damage(string key, string version)
    {
        lock (_gate)
        {
            var versions = Versions(key);
            var index = versions.FindIndex(stored => stored.VersionId == version);
            var bytes = versions[index].Bytes.ToArray();
            bytes[bytes.Length / 2] ^= 0x5A;
            versions[index] = versions[index] with { Bytes = bytes };
        }
    }

    public void Damage(SiloExactLocator locator) => Damage(locator.Key, locator.VersionId);

    public void SetVersionUnavailable(SiloExactLocator locator, bool unavailable)
    {
        lock (_gate)
        {
            if (unavailable) _unavailableVersions.Add(locator);
            else _unavailableVersions.Remove(locator);
        }
    }

    public IReadOnlyList<string> Keys(string prefix)
    {
        lock (_gate)
        {
            return _objects.Where(pair => pair.Value.Count > 0 && pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(static pair => pair.Key).ToArray();
        }
    }

    /// <summary>A digest over every key, version, byte and header, to prove an operation mutated nothing.</summary>
    public string Fingerprint()
    {
        lock (_gate)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var (key, versions) in _objects)
            {
                foreach (var stored in versions)
                {
                    hash.AppendData(Encoding.UTF8.GetBytes(key + "\n" + stored.VersionId + "\n"));
                    hash.AppendData(stored.Bytes);
                    foreach (var (name, value) in stored.Metadata.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                    {
                        hash.AppendData(Encoding.UTF8.GetBytes(name + "=" + value + "\n"));
                    }
                }
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
    }

    private List<StoredVersion> Versions(string key)
    {
        if (!_objects.TryGetValue(key, out var versions))
        {
            versions = [];
            _objects[key] = versions;
        }
        return versions;
    }

    private string NextVersionLocked() => $"{Descriptor.StoreId}-v{++_counter:D5}";

    private void ThrowIfUnreachable(string operation)
    {
        if (Unreachable)
        {
            throw new SiloStoreUnavailableException(Descriptor.StoreId, operation, writeMayHaveLanded: false);
        }
    }
}

/// <summary>The payload spool seam (#1968 owns the real spool): exact staged bytes by token, and release tracking.</summary>

internal sealed class ServedStream(byte[] bytes, Action<int> served) : Stream
{
    private readonly MemoryStream _inner = new(bytes, writable: false);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(_inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(_inner.Read(buffer));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Count(_inner.Read(buffer, offset, count)));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Count(_inner.Read(buffer.Span)));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    private int Count(int read)
    {
        served(read);
        return read;
    }
}

/// <summary>Throws once at the armed boundary, as a process crash there would stop the work.</summary>
