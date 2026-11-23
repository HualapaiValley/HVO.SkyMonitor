using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>Object keys of the instance bucket (#1965 4.3). Zero-padded so lexical order is journal order.</summary>
internal static class SiloObjectNamespace
{
    public const string JournalPrefix = "j/";
    public const string CheckpointPrefix = "c/";

    public static string PayloadKey(SiloRetentionClass retentionClass, string sha256)
    {
        SiloHash.Require(sha256, nameof(sha256));
        var prefix = retentionClass switch
        {
            SiloRetentionClass.Full => "p/full/",
            SiloRetentionClass.Display => "p/display/",
            SiloRetentionClass.Metadata => "p/meta/",
            _ => throw new ArgumentOutOfRangeException(nameof(retentionClass))
        };
        return string.Concat(prefix, sha256.AsSpan(0, 2), "/", sha256);
    }

    /// <summary>
    /// The namespace of one logical group revision's manifests, partitioned by the group's UTC anchor (#1965 4.3). Each
    /// physical manifest lies directly beneath it under its own exact digest.
    /// </summary>
    public static string ManifestRevisionPrefix(DateTimeOffset anchorUtc, string groupId, string revisionId)
    {
        SiloText.RequireToken(groupId, nameof(groupId), 128);
        SiloText.RequireToken(revisionId, nameof(revisionId), 128);
        var utc = anchorUtc.UtcDateTime;
        return string.Create(CultureInfo.InvariantCulture, $"m/{utc:yyyy}/{utc:MM}/{utc:dd}/{groupId}/{revisionId}/");
    }

    /// <summary>
    /// The immutable physical key of one exact manifest of a logical group revision. The key is fixed by the manifest's
    /// own SHA-256, so a manifest rebuilt before P3 with different bytes never contends for the key of one it supersedes,
    /// and a retry of the same bytes always targets the same key.
    /// </summary>
    public static string ManifestKey(DateTimeOffset anchorUtc, string groupId, string revisionId, string manifestSha256)
    {
        SiloHash.Require(manifestSha256, nameof(manifestSha256));
        return string.Concat(ManifestRevisionPrefix(anchorUtc, groupId, revisionId), manifestSha256, ".json");
    }

    public static string SegmentKey(SiloJournalPosition position) =>
        PositionKey(JournalPrefix, position);

    public static string CheckpointKey(SiloJournalPosition covers) =>
        PositionKey(CheckpointPrefix, covers);

    public static bool TryParseSegmentKey(string key, out SiloJournalPosition position) =>
        TryParsePositionKey(JournalPrefix, key, out position);

    public static bool TryParseCheckpointKey(string key, out SiloJournalPosition position) =>
        TryParsePositionKey(CheckpointPrefix, key, out position);

    private static string PositionKey(string prefix, SiloJournalPosition position)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(position.Generation, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position.Generation, 999_999);
        ArgumentOutOfRangeException.ThrowIfLessThan(position.Sequence, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position.Sequence, 999_999_999_999);
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}g{position.Generation:D6}/{position.Sequence:D12}.json");
    }

    private static bool TryParsePositionKey(string prefix, string key, out SiloJournalPosition position)
    {
        position = default;
        // <prefix>gNNNNNN/NNNNNNNNNNNN.json
        if (key.Length != prefix.Length + 1 + 6 + 1 + 12 + 5 || !key.StartsWith(prefix, StringComparison.Ordinal) ||
            key[prefix.Length] != 'g' || key[prefix.Length + 7] != '/' || !key.EndsWith(".json", StringComparison.Ordinal))
        {
            return false;
        }
        if (!long.TryParse(key.AsSpan(prefix.Length + 1, 6), NumberStyles.None, CultureInfo.InvariantCulture, out var generation) ||
            !long.TryParse(key.AsSpan(prefix.Length + 8, 12), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ||
            generation < 1 || sequence < 1)
        {
            return false;
        }
        position = new(generation, sequence);
        return true;
    }
}

/// <summary>
/// Stable object headers written once at creation. They identify and help diagnose an object, but they are stored
/// metadata: they never prove bytes (#1965 F13). Frequently updated processing metadata is never published as headers.
/// </summary>
internal static class SiloObjectHeaders
{
    /// <summary>Hex SHA-256 of the exact bytes, beside the supplied S3 checksum (<c>x-amz-checksum-sha256</c>).</summary>
    public const string Sha256 = "hvo-sha256";
    public const string Format = "hvo-format";
    public const string Binding = "hvo-binding";
}

/// <summary>A qualified store's identity. Persistence qualification is a property of the store's profile (#1965 5.2).</summary>
internal sealed record SiloStoreDescriptor(string StoreId, string Bucket, SiloStoreClass StoreClass, SiloRetrieval Retrieval, bool PersistenceQualified);

internal sealed record SiloPutRequest(
    string Key,
    long Length,
    string Sha256,
    Func<CancellationToken, ValueTask<Stream>> OpenContent,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>The store refused a write whose bytes did not match the supplied checksum; nothing was created.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Internal Silo exception; every instance carries the evidence its handlers need.")]
internal sealed class SiloChecksumRejectedException(string key)
    : IOException($"The store refused {key}: its bytes do not match the supplied SHA-256 checksum.")
{
    public string Key { get; } = key;
}

/// <summary><c>Created</c> with the new version ID, or not created because the key already has a current version.</summary>
internal sealed record SiloPutResult(bool Created, string? VersionId);

internal enum SiloOpenStatus { Found, NoSuchVersion, NoSuchKey }

internal sealed class SiloOpenResult(SiloOpenStatus status, Stream? content) : IDisposable, IAsyncDisposable
{
    public SiloOpenStatus Status { get; } = status;
    public Stream? Content { get; } = content;

    public void Dispose() => Content?.Dispose();

    public ValueTask DisposeAsync() => Content?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>
/// The narrow seam to one versioned bucket. #1966 binds the stock Silo; tests bind a generated local fixture.
/// Every failure to reach the store, including a timeout, is a <see cref="SiloStoreUnavailableException"/>.
/// </summary>
internal interface ISiloObjectStore
{
    SiloStoreDescriptor Descriptor { get; }

    /// <summary>
    /// Create the key once (<c>If-None-Match: *</c>) with the supplied SHA-256 checksum. An existing current version is
    /// reported, never replaced.
    /// </summary>
    ValueTask<SiloPutResult> PutCreateOnlyAsync(SiloPutRequest request, CancellationToken cancellationToken);

    /// <summary>GET one exact version. Its bytes prove nothing until the caller hashes them.</summary>
    ValueTask<SiloOpenResult> OpenVersionAsync(string key, string versionId, CancellationToken cancellationToken);

    /// <summary>Locate the key's current version ID (a HEAD); null when the key has none. Never proof of bytes.</summary>
    ValueTask<string?> LocateCurrentVersionAsync(string key, CancellationToken cancellationToken);

    /// <summary>Keys under a prefix in ascending ordinal order, strictly after <paramref name="startAfter"/> when given.</summary>
    IAsyncEnumerable<string> ListKeysAsync(string prefix, string? startAfter, CancellationToken cancellationToken);
}

/// <summary>The stores a catalog may read: its own local store first, then any archive or colder stores.</summary>
internal sealed class SiloStoreSet
{
    private readonly Dictionary<(string, string), ISiloObjectStore> _stores = new();

    public SiloStoreSet(ISiloObjectStore local, params IEnumerable<ISiloObjectStore> others)
    {
        ArgumentNullException.ThrowIfNull(local);
        Local = local;
        Add(local);
        foreach (var store in others)
        {
            Add(store);
        }
    }

    public ISiloObjectStore Local { get; }

    public IEnumerable<ISiloObjectStore> All => _stores.Values;

    public bool TryResolve(string storeId, string bucket, out ISiloObjectStore store) =>
        _stores.TryGetValue((storeId, bucket), out store!);

    private void Add(ISiloObjectStore store)
    {
        var descriptor = store.Descriptor;
        if (!_stores.TryAdd((descriptor.StoreId, descriptor.Bucket), store))
        {
            throw new ArgumentException("Each store and bucket is configured once.", nameof(store));
        }
    }
}

internal enum SiloProbeStatus { Verified, Absent, Damaged, Unavailable }

/// <summary>The physical outcome of reading one exact version and hashing it independently.</summary>
internal sealed record SiloProbe(SiloProbeStatus Status, SiloExactLocator Locator, DateTimeOffset ObservedUtc, string Detail)
{
    public byte[]? Bytes { get; init; }
}

internal static class SiloVerifier
{
    /// <summary>
    /// Physical proof (#1965 6.2): GET the exact version and compute its length and SHA-256 over the returned bytes.
    /// HEAD and stored checksums are never accepted. An unknown store or an unreachable one cannot be checked.
    /// </summary>
    public static async ValueTask<SiloProbe> ProbeAsync(
        SiloStoreSet stores, SiloExactLocator locator, long expectedLength, string expectedSha256,
        TimeProvider clock, bool captureBytes, CancellationToken cancellationToken)
    {
        if (!stores.TryResolve(locator.StoreId, locator.Bucket, out var store))
        {
            return new(SiloProbeStatus.Unavailable, locator, clock.GetUtcNow(), "store-not-configured");
        }
        return await ProbeAsync(store, locator, expectedLength, expectedSha256, clock, captureBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async ValueTask<SiloProbe> ProbeAsync(
        ISiloObjectStore store, SiloExactLocator locator, long expectedLength, string expectedSha256,
        TimeProvider clock, bool captureBytes, CancellationToken cancellationToken)
    {
        try
        {
            using var opened = await store.OpenVersionAsync(locator.Key, locator.VersionId, cancellationToken)
                .ConfigureAwait(false);
            if (opened.Status != SiloOpenStatus.Found || opened.Content is null)
            {
                return new(SiloProbeStatus.Absent, locator, clock.GetUtcNow(),
                    opened.Status == SiloOpenStatus.NoSuchKey ? "no-such-key" : "no-such-version");
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var captured = captureBytes ? new MemoryStream() : null;
            var buffer = new byte[81920];
            long length = 0;
            try
            {
                int read;
                while ((read = await opened.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    length += read;
                    if (length > expectedLength)
                    {
                        return new(SiloProbeStatus.Damaged, locator, clock.GetUtcNow(), "length-exceeds-identity");
                    }
                    hash.AppendData(buffer, 0, read);
                    if (captured is not null)
                    {
                        await captured.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (IOException exception) when (exception is not SiloStoreUnavailableException)
            {
                return new(SiloProbeStatus.Damaged, locator, clock.GetUtcNow(), "read-error");
            }
            if (length != expectedLength)
            {
                return new(SiloProbeStatus.Damaged, locator, clock.GetUtcNow(), "length-mismatch");
            }
            var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(sha, expectedSha256, StringComparison.Ordinal))
            {
                return new(SiloProbeStatus.Damaged, locator, clock.GetUtcNow(), "sha256-mismatch");
            }
            return new(SiloProbeStatus.Verified, locator, clock.GetUtcNow(), SiloFormats.ExactGetSha256)
            {
                Bytes = captured?.ToArray()
            };
        }
        catch (SiloStoreUnavailableException)
        {
            return new(SiloProbeStatus.Unavailable, locator, clock.GetUtcNow(), "store-unavailable");
        }
    }

    /// <summary>Read the current version of a small metadata object, for create-only conflict resolution and journal reads.</summary>
    public static ValueTask<(string VersionId, byte[] Bytes)?> ReadCurrentAsync(
        ISiloObjectStore store, string key, CancellationToken cancellationToken) =>
        ReadCurrentAsync(store, key, SiloJson.MaximumMetadataObjectBytes, cancellationToken);

    /// <summary>
    /// Read the current version of a metadata object no larger than <paramref name="maximumBytes"/>. The read stops at the
    /// first byte past the bound, so an oversized object costs at most one byte more than the bound to refuse.
    /// </summary>
    public static async ValueTask<(string VersionId, byte[] Bytes)?> ReadCurrentAsync(
        ISiloObjectStore store, string key, long maximumBytes, CancellationToken cancellationToken)
    {
        var versionId = await store.LocateCurrentVersionAsync(key, cancellationToken).ConfigureAwait(false);
        if (versionId is null)
        {
            return null;
        }
        using var opened = await store.OpenVersionAsync(key, versionId, cancellationToken).ConfigureAwait(false);
        if (opened.Status != SiloOpenStatus.Found || opened.Content is null)
        {
            return null;
        }
        using var buffer = new MemoryStream();
        var chunk = new byte[(int)Math.Min(81920, maximumBytes + 1)];
        int read;
        while ((read = await opened.Content.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, maximumBytes + 1 - buffer.Length)),
            cancellationToken).ConfigureAwait(false)) > 0)
        {
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            if (buffer.Length > maximumBytes)
            {
                throw new SiloInvalidObjectException(key, "the metadata object exceeds its bound");
            }
        }
        return (versionId, buffer.ToArray());
    }
}
