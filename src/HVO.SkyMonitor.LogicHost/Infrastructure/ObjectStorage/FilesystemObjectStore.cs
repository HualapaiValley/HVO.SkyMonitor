using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Infrastructure.ObjectStorage;

/// <summary>
/// The production filesystem implementation of <see cref="IObjectStore"/>, built on the #592
/// primitives. One process, one LogicHost replica, one dedicated root beneath which the two
/// deployment-precreated bucket directories live. Every operation is bounded per key by a
/// keyed lock so that put, copy, and delete on the same key serialize, while operations on
/// different keys proceed concurrently; reads take no lock, because a committed data file is
/// immutable and a reader that opened it keeps it even if the descriptor moves on.
/// </summary>
/// <remarks>
/// <para>Commit point: the descriptor. A put streams bytes to a temporary in the key's own
/// directory, flushes them, renames them onto the immutable data name for a fresh generation,
/// flushes the directory, then publishes the descriptor atomically. A crash before the
/// descriptor publishes leaves either nothing or an orphan data file; either way the key's
/// prior state is intact. A crash after leaves the previous generation's data file as a
/// retired orphan. Both are reclaimed by reconciliation (later slice), never by a request.</para>
/// <para>Failure facts from the primitives are mapped to the neutral kinds: containment is
/// <see cref="ObjectStoreFailureKind.Unsupported"/> (the deployment is misconfigured, retrying
/// does not help); NoSpace is <see cref="ObjectStoreFailureKind.Capacity"/>; a descriptor that
/// contradicts its data is <see cref="ObjectStoreFailureKind.CorruptState"/>.</para>
/// </remarks>
[SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Provider logs through the shared LoggerMessage partials below.")]
internal sealed partial class FilesystemObjectStore : IObjectStore, IDisposable
{
    private const int StreamBufferSize = 64 * 1024;
    private const string TemporaryDataSuffix = ".tmp";

    private readonly PhysicalRoot _root;
    private readonly ObjectStoreTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FilesystemObjectStore> _logger;
    private readonly FileStream _runtimeLock;
    internal bool DisableHardLinksForTest { get; set; }
    private long _hardLinkCopyCount;
    internal Func<Task>? AfterHardLinkForTest { get; set; }
    internal long HardLinkCopyCount => Interlocked.Read(ref _hardLinkCopyCount);
    // Per-key serialization through a fixed stripe of locks, selected by the key hash. A
    // dictionary keyed by every key ever written would grow with the object count for the
    // life of the process; 1,024 stripes bound that at a constant while keeping contention
    // between unrelated keys negligible for the supported single-replica topology. Two keys
    // that share a stripe serialize needlessly but never incorrectly.
    private const int LockStripes = 1024;
    private readonly SemaphoreSlim[] _keyLocks = Enumerable.Range(0, LockStripes).Select(static _ => new SemaphoreSlim(1, 1)).ToArray();

    public FilesystemObjectStore(
        IOptions<CentralObjectStorageOptions> options,
        ObjectStoreTelemetry telemetry,
        TimeProvider timeProvider,
        ILogger<FilesystemObjectStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = options.Value.Filesystem.Root
            ?? throw new InvalidOperationException("ObjectStorage:Filesystem:Root is required for the filesystem provider.");
        _root = PhysicalRoot.Open(root);
        _runtimeLock = FilesystemObjectBackup.AcquireRuntimeLock(_root);
        try
        {
            if (FilesystemObjectBackup.HasUnresolvedRestore(_root))
            {
                throw new InvalidOperationException(
                    "Object storage has an unresolved destructive restore. Run the offline object-store restore command to recover it before starting LogicHost.");
            }
        }
        catch
        {
            _runtimeLock.Dispose();
            throw;
        }
        _telemetry = telemetry;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The physical root; exposed for reconciliation and tests.</summary>
    internal PhysicalRoot Root => _root;

    public void Dispose()
    {
        _runtimeLock.Dispose();
        foreach (var keyLock in _keyLocks)
        {
            keyLock.Dispose();
        }
    }

    public Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken)
        => ExecuteAsync("bucket-exists", bucket, _ =>
        {
            // Buckets are precreated by deployment; the provider never creates one. A missing
            // bucket is a deployment fault the health check surfaces, not something to repair.
            // A bucket path that fails containment (a symbolic link where a directory should
            // be) is answered "no such bucket" rather than raised: the contract asks whether a
            // usable bucket exists, and one that would escape the root is not usable.
            try
            {
                var path = _root.Resolve(FilesystemObjectLayout.BucketRelativePath(bucket));
                return Task.FromResult(Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null);
            }
            catch (FileSystemFaultException fault) when (fault.Kind == FileSystemFaultKind.Containment)
            {
                return Task.FromResult(false);
            }
        }, cancellationToken);

    public async Task PutAsync(
        string bucket,
        string key,
        Stream content,
        long contentLength,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (contentLength < 0 || contentLength > IObjectStore.MaximumPutContentLength)
        {
            throw new ObjectStoreException(ObjectStoreFailureKind.Unsupported, "put");
        }
        if (string.IsNullOrWhiteSpace(contentType))
        {
            contentType = "application/octet-stream";
        }

        _telemetry.ChangeActiveStreams("write", 1);
        try
        {
            await ExecuteAsync("put", bucket, async token =>
            {
                await RequireBucketAsync(bucket, "put", token).ConfigureAwait(false);
                var keyHash = FilesystemObjectLayout.KeyHash(key);
                using var _ = await LockKeyAsync(bucket, keyHash, token).ConfigureAwait(false);
                var generation = FilesystemObjectLayout.NewGeneration();
                var (length, sha256) = await WriteDataAsync(bucket, keyHash, generation, content, contentLength, token).ConfigureAwait(false);
                var descriptor = new FilesystemObjectDescriptor(
                    FilesystemObjectLayout.SchemaVersion, key, contentType, length, sha256, generation,
                    _timeProvider.GetUtcNow());
                await PublishDescriptorAsync(bucket, keyHash, descriptor, token).ConfigureAwait(false);
                _telemetry.RecordBytes("put", "write", bucket, length);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _telemetry.ChangeActiveStreams("write", -1);
        }
    }

    public Task<ObjectStoreObjectMetadata> StatAsync(string bucket, string key, CancellationToken cancellationToken)
        => ExecuteAsync("stat", bucket, async token =>
        {
            var descriptor = await ReadDescriptorAsync(bucket, key, "stat", token).ConfigureAwait(false);
            return new ObjectStoreObjectMetadata(key, descriptor.Length, descriptor.ContentType, descriptor.Generation, descriptor.ModifiedUtc);
        }, cancellationToken);

    public async Task ReadAsync(
        string bucket,
        string key,
        string? generation,
        Func<Stream, CancellationToken, Task> reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _telemetry.ChangeActiveStreams("read", 1);
        try
        {
            await ExecuteAsync("read", bucket, async token =>
            {
                var descriptor = await ReadDescriptorAsync(bucket, key, "read", token).ConfigureAwait(false);
                if (generation is not null && !string.Equals(generation, descriptor.Generation, StringComparison.Ordinal))
                {
                    // The requested generation is no longer current. Its data file may still exist
                    // (retired, awaiting reclamation), but serving it would let a caller read what
                    // the descriptor no longer names; the neutral contract says Precondition.
                    throw new ObjectStoreException(ObjectStoreFailureKind.Precondition, "read");
                }
                var keyHash = FilesystemObjectLayout.KeyHash(key);
                var dataPath = _root.Resolve(FilesystemObjectLayout.DataRelativePath(bucket, keyHash, descriptor.Generation));
                FileStream stream;
                try
                {
                    stream = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, StreamBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                catch (FileNotFoundException exception)
                {
                    // Descriptor present, data absent: the store contradicts itself.
                    throw new ObjectStoreException(ObjectStoreFailureKind.CorruptState, "read", exception);
                }
                await using (stream.ConfigureAwait(false))
                {
                    if (stream.Length != descriptor.Length)
                    {
                        throw new ObjectStoreException(ObjectStoreFailureKind.CorruptState, "read");
                    }
                    // Readers get a forward-only view, as they do from every networked provider,
                    // and one bounded to the descriptor's length so a data file that somehow grew
                    // cannot leak bytes the descriptor never committed.
                    var view = new ForwardOnlyReadStream(stream, descriptor.Length);
                    await using (view.ConfigureAwait(false))
                    {
                        try
                        {
                            await reader(view, token).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            throw new ReaderCallbackException(exception);
                        }
                    }
                    _telemetry.RecordBytes("read", "read", bucket, descriptor.Length);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _telemetry.ChangeActiveStreams("read", -1);
        }
    }

    public Task CopyAsync(string bucket, string sourceKey, string destinationKey, CancellationToken cancellationToken)
        => ExecuteAsync("copy", bucket, async token =>
        {
            await RequireBucketAsync(bucket, "copy", token).ConfigureAwait(false);
            var source = await ReadDescriptorAsync(bucket, sourceKey, "copy", token).ConfigureAwait(false);
            await CopyFromDescriptorAsync(bucket, sourceKey, destinationKey, source, token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Test seam: run the copy body from an already-read source descriptor, to pin the reclaim race deterministically.</summary>
    internal Task CopyFromDescriptorForTestAsync(string bucket, string sourceKey, string destinationKey, FilesystemObjectDescriptor source, CancellationToken cancellationToken)
        => ExecuteAsync("copy", bucket, token => CopyFromDescriptorAsync(bucket, sourceKey, destinationKey, source, token), cancellationToken);

    private async Task CopyFromDescriptorAsync(string bucket, string sourceKey, string destinationKey, FilesystemObjectDescriptor source, CancellationToken token)
    {
        var sourceHash = FilesystemObjectLayout.KeyHash(sourceKey);
        var destinationHash = FilesystemObjectLayout.KeyHash(destinationKey);
        var sourceData = _root.Resolve(FilesystemObjectLayout.DataRelativePath(bucket, sourceHash, source.Generation));

        using var _ = await LockKeyAsync(bucket, destinationHash, token).ConfigureAwait(false);
        var generation = FilesystemObjectLayout.NewGeneration();
        var sourceRelative = FilesystemObjectLayout.DataRelativePath(bucket, sourceHash, source.Generation);
        var destinationRelative = FilesystemObjectLayout.DataRelativePath(bucket, destinationHash, generation);
        var destinationData = _root.Resolve(destinationRelative);
        HardLinkPublication? linked = null;
        try
        {
            linked = DisableHardLinksForTest
                ? null
                : await HardLinkPublisher.TryPublishAsync(_root, sourceRelative, destinationRelative, token).ConfigureAwait(false);
        }
        catch (FileSystemFaultException fault) when (fault.Kind == FileSystemFaultKind.NotFound)
        {
            await ThrowCopySourceMovedAsync(bucket, sourceKey, source, fault, token).ConfigureAwait(false);
        }
        if (linked is not null)
        {
            using (linked)
            {
                try
                {
                    if (linked.Length != source.Length || !string.Equals(linked.Sha256, source.Sha256, StringComparison.Ordinal))
                    {
                        linked.DeleteUncommitted();
                        throw new ObjectStoreException(ObjectStoreFailureKind.CorruptState, "copy");
                    }
                    linked.VerifyCurrent();
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or UnauthorizedAccessException or FileSystemFaultException)
                {
                    linked.DeleteUncommitted();
                    if (exception is FileSystemFaultException fault)
                    {
                        throw Map("copy", fault);
                    }
                    throw;
                }
                Interlocked.Increment(ref _hardLinkCopyCount);
                if (AfterHardLinkForTest is { } afterHardLink)
                {
                    await afterHardLink().ConfigureAwait(false);
                }
                // Do not unlink after descriptor publication starts: a rename may have committed
                // before a later directory-sync failure. As on the streaming path, an unreferenced
                // linked generation is safe reconciliation debris; missing committed data is not.
                var linkedDescriptor = source with { Key = destinationKey, Generation = generation, ModifiedUtc = _timeProvider.GetUtcNow() };
                await PublishDescriptorAsync(bucket, destinationHash, linkedDescriptor, token).ConfigureAwait(false);
            }
            return;
        }

        FileStream sourceStream;
        try
        {
            sourceStream = new FileStream(sourceData, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, StreamBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException exception)
        {
            await ThrowCopySourceMovedAsync(bucket, sourceKey, source, exception, token).ConfigureAwait(false);
            throw;
        }
        (long length, string sha256) written;
        await using (sourceStream.ConfigureAwait(false))
        {
            written = await WriteDataAsync(bucket, destinationHash, generation, sourceStream, source.Length, token).ConfigureAwait(false);
        }
        if (!string.Equals(written.sha256, source.Sha256, StringComparison.Ordinal))
        {
            // The source data no longer matches its descriptor's digest: local corruption.
            TryDelete(_root.Resolve(FilesystemObjectLayout.DataRelativePath(bucket, destinationHash, generation)));
            throw new ObjectStoreException(ObjectStoreFailureKind.CorruptState, "copy");
        }
        var descriptor = source with { Key = destinationKey, Generation = generation, ModifiedUtc = _timeProvider.GetUtcNow() };
        await PublishDescriptorAsync(bucket, destinationHash, descriptor, token).ConfigureAwait(false);
    }

    private async Task ThrowCopySourceMovedAsync(
        string bucket,
        string sourceKey,
        FilesystemObjectDescriptor source,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // The source data named by the descriptor we read is gone. Re-read the descriptor
        // to distinguish source movement from a store that contradicts itself.
        FilesystemObjectDescriptor? again;
        try
        {
            again = await ReadDescriptorAsync(bucket, sourceKey, "copy", cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectStoreException recheck) when (recheck.Kind == ObjectStoreFailureKind.MissingObject)
        {
            throw new ObjectStoreException(ObjectStoreFailureKind.MissingObject, "copy", exception);
        }
        if (!string.Equals(again.Generation, source.Generation, StringComparison.Ordinal))
        {
            throw new ObjectStoreException(ObjectStoreFailureKind.Precondition, "copy", exception);
        }
        throw new ObjectStoreException(ObjectStoreFailureKind.CorruptState, "copy", exception);
    }

    public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken)
        => ExecuteAsync("delete", bucket, async token =>
        {
            await RequireBucketAsync(bucket, "delete", token).ConfigureAwait(false);
            var keyHash = FilesystemObjectLayout.KeyHash(key);
            using var _ = await LockKeyAsync(bucket, keyHash, token).ConfigureAwait(false);
            var descriptorPath = _root.Resolve(FilesystemObjectLayout.DescriptorRelativePath(bucket, keyHash));
            // Idempotent: a missing descriptor is success. Deleting the descriptor is the commit;
            // the data file becomes retired and is reclaimed by reconciliation, so an open reader
            // keeps what it opened.
            try
            {
                if (File.Exists(descriptorPath))
                {
                    _root.Verify(descriptorPath, "delete");
                    File.Delete(descriptorPath);
                    DurableSync.Directory(Path.GetDirectoryName(descriptorPath)!);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw Map("delete", FileSystemFaultException.From("delete", descriptorPath, exception));
            }
        }, cancellationToken);

    public async IAsyncEnumerable<ObjectStoreItem> ListAsync(
        string bucket,
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        string? startAfter = null)
    {
        var started = _timeProvider.GetTimestamp();
        using var activity = _telemetry.StartOperation("list", bucket);
        List<ObjectStoreItem> items;
        try
        {
            await RequireBucketAsync(bucket, "list", cancellationToken).ConfigureAwait(false);
            // The physical layout is hashed, so listing is a full scan of the bucket's
            // descriptors, filtered and sorted by the exact logical key. That is O(bucket) per
            // list; the supported topology's bucket sizes make this acceptable and the
            // conformance suite's >1,000-item case proves ordering and completeness.
            items = await Task.Run(() => ScanBucket(bucket, prefix, startAfter, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _telemetry.RecordOperation("list", "canceled", bucket, _timeProvider.GetElapsedTime(started));
            throw;
        }
        catch (ObjectStoreException exception)
        {
            _telemetry.RecordOperation("list", ObjectStoreException.GetOutcome(exception.Kind), bucket, _timeProvider.GetElapsedTime(started));
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FileSystemFaultException)
        {
            var mapped = Map("list", exception is FileSystemFaultException fault ? fault : FileSystemFaultException.From("list", bucket, exception));
            _telemetry.RecordOperation("list", ObjectStoreException.GetOutcome(mapped.Kind), bucket, _timeProvider.GetElapsedTime(started));
            throw mapped;
        }
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
        _telemetry.RecordOperation("list", "success", bucket, _timeProvider.GetElapsedTime(started));
    }

    private List<ObjectStoreItem> ScanBucket(string bucket, string prefix, string? startAfter, CancellationToken cancellationToken)
    {
        var bucketPath = _root.Resolve(FilesystemObjectLayout.BucketRelativePath(bucket));
        var items = new List<ObjectStoreItem>();
        var quarantineMarker = Path.DirectorySeparatorChar + FilesystemObjectReconciler.QuarantineDirectoryName + Path.DirectorySeparatorChar;
        foreach (var descriptorPath in Directory.EnumerateFiles(bucketPath, "*" + FilesystemObjectLayout.DescriptorSuffix, new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (descriptorPath.Contains(quarantineMarker, StringComparison.Ordinal))
            {
                continue; // quarantined descriptors are the operator's, not the store's
            }
            FilesystemObjectDescriptor? descriptor;
            try
            {
                descriptor = JsonSerializer.Deserialize<FilesystemObjectDescriptor>(File.ReadAllBytes(descriptorPath), FilesystemObjectLayout.DescriptorJson);
            }
            catch (JsonException)
            {
                // A malformed descriptor is not a listable object; reconciliation quarantines it.
                continue;
            }
            if (descriptor is null || descriptor.Schema != FilesystemObjectLayout.SchemaVersion)
            {
                continue;
            }
            if (!descriptor.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }
            if (startAfter is not null && StringComparer.Ordinal.Compare(descriptor.Key, startAfter) <= 0)
            {
                continue;
            }
            items.Add(new ObjectStoreItem(descriptor.Key, descriptor.Length, descriptor.ModifiedUtc));
        }
        items.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));
        return items;
    }

    private async Task<(long Length, string Sha256)> WriteDataAsync(
        string bucket, string keyHash, string generation, Stream content, long contentLength, CancellationToken cancellationToken)
    {
        var dataRelative = FilesystemObjectLayout.DataRelativePath(bucket, keyHash, generation);
        var dataPath = _root.Resolve(dataRelative);
        AtomicPublisher.EnsureDirectory(_root, Path.GetDirectoryName(dataPath)!);
        var temporaryPath = dataPath + "." + Guid.NewGuid().ToString("N") + TemporaryDataSuffix;
        long written = 0;
        string sha256;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            try
            {
                var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, StreamBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
                await using (stream.ConfigureAwait(false))
                {
                    var buffer = new byte[StreamBufferSize];
                    while (written < contentLength)
                    {
                        var wanted = (int)Math.Min(buffer.Length, contentLength - written);
                        var read = await content.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }
                        await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        written += read;
                    }
                    if (written != contentLength)
                    {
                        // Short input: declared length not delivered. Never commit a shorter object.
                        throw new ObjectStoreException(ObjectStoreFailureKind.Unsupported, "put");
                    }
                    // Long input: one more byte available than declared. Refuse rather than truncate.
                    var probe = new byte[1];
                    if (await content.ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
                    {
                        throw new ObjectStoreException(ObjectStoreFailureKind.Unsupported, "put");
                    }
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    DurableSync.File(stream.SafeFileHandle, temporaryPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw Map("put", FileSystemFaultException.From("put-write", dataRelative, exception));
            }
            sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());

            _root.Verify(Path.GetDirectoryName(dataPath)!, "put-rename");
            try
            {
                File.Move(temporaryPath, dataPath, overwrite: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw Map("put", FileSystemFaultException.From("put-rename", dataRelative, exception));
            }
            _root.Verify(dataPath, "put-rename");
            DurableSync.Directory(Path.GetDirectoryName(dataPath)!);
            return (written, sha256);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private async Task PublishDescriptorAsync(string bucket, string keyHash, FilesystemObjectDescriptor descriptor, CancellationToken cancellationToken)
    {
        var relative = FilesystemObjectLayout.DescriptorRelativePath(bucket, keyHash);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(descriptor, FilesystemObjectLayout.DescriptorJson);
        try
        {
            await AtomicPublisher.PublishAsync(_root, relative, PublishMode.Replace,
                (stream, token) => stream.WriteAsync(bytes, token).AsTask(), cancellationToken).ConfigureAwait(false);
        }
        catch (FileSystemFaultException fault)
        {
            // The data file for this generation is committed but unreferenced; reconciliation reclaims it.
            throw Map("put", fault);
        }
    }

    private async Task<FilesystemObjectDescriptor> ReadDescriptorAsync(string bucket, string key, string operation, CancellationToken cancellationToken)
    {
        var keyHash = FilesystemObjectLayout.KeyHash(key);
        string descriptorPath;
        try
        {
            descriptorPath = _root.Resolve(FilesystemObjectLayout.DescriptorRelativePath(bucket, keyHash));
        }
        catch (FileSystemFaultException fault)
        {
            throw Map(operation, fault);
        }
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(descriptorPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            if (!await BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
            {
                throw new ObjectStoreException(ObjectStoreFailureKind.MissingBucket, operation, exception);
            }
            throw new ObjectStoreException(ObjectStoreFailureKind.MissingObject, operation, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Map(operation, FileSystemFaultException.From(operation, descriptorPath, exception));
        }
        FilesystemObjectDescriptor? descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<FilesystemObjectDescriptor>(bytes, FilesystemObjectLayout.DescriptorJson);
        }
        catch (JsonException exception)
        {
            throw new ObjectStoreException(ObjectStoreFailureKind.CorruptState, operation, exception);
        }
        if (descriptor is null || !descriptor.IsWellFormed(key))
        {
            // Malformed, wrong schema, or names a different key (hash collision or tampering).
            throw new ObjectStoreException(ObjectStoreFailureKind.CorruptState, operation);
        }
        return descriptor;
    }

    private async Task RequireBucketAsync(string bucket, string operation, CancellationToken cancellationToken)
    {
        if (!await BucketExistsAsync(bucket, cancellationToken).ConfigureAwait(false))
        {
            throw new ObjectStoreException(ObjectStoreFailureKind.MissingBucket, operation);
        }
    }

    private async Task<IDisposable> LockKeyAsync(string bucket, string keyHash, CancellationToken cancellationToken)
    {
        var gate = _keyLocks[GetLockStripe(bucket, keyHash)];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Release(gate);
    }

    internal IDisposable LockKeyForMaintenance(string bucket, string keyHash, CancellationToken cancellationToken)
    {
        var gate = _keyLocks[GetLockStripe(bucket, keyHash)];
        gate.Wait(cancellationToken);
        return new Release(gate);
    }

    private static int GetLockStripe(string bucket, string keyHash)
    {
        // The hash is uniform hex, so its leading bits pick a stripe evenly; the bucket is
        // folded in so the same key in two buckets does not always share a stripe.
        return (int)((uint)BitConverter.ToInt32(Convert.FromHexString(keyHash.AsSpan(0, 8)))
            ^ (uint)StringComparer.Ordinal.GetHashCode(bucket)) & (LockStripes - 1);
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private async Task<T> ExecuteAsync<T>(string operation, string bucket, Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        using var activity = _telemetry.StartOperation(operation, bucket);
        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            _telemetry.RecordOperation(operation, "success", bucket, _timeProvider.GetElapsedTime(started));
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _telemetry.RecordOperation(operation, "canceled", bucket, _timeProvider.GetElapsedTime(started));
            throw;
        }
        catch (ReaderCallbackException exception)
        {
            _telemetry.RecordOperation(operation, "caller-failure", bucket, _timeProvider.GetElapsedTime(started));
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException!).Throw();
            throw;
        }
        catch (ObjectStoreException exception)
        {
            _telemetry.RecordOperation(operation, ObjectStoreException.GetOutcome(exception.Kind), bucket, _timeProvider.GetElapsedTime(started));
            LogFailure(_logger, operation, ObjectStoreException.GetOutcome(exception.Kind), exception.Kind is ObjectStoreFailureKind.CorruptState or ObjectStoreFailureKind.Capacity ? exception : null);
            throw;
        }
        catch (FileSystemFaultException fault)
        {
            var mapped = Map(operation, fault);
            _telemetry.RecordOperation(operation, ObjectStoreException.GetOutcome(mapped.Kind), bucket, _timeProvider.GetElapsedTime(started));
            LogFailure(_logger, operation, ObjectStoreException.GetOutcome(mapped.Kind), null);
            throw mapped;
        }
    }

    private async Task ExecuteAsync(string operation, string bucket, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
        => await ExecuteAsync<object?>(operation, bucket, async token => { await action(token).ConfigureAwait(false); return null; }, cancellationToken).ConfigureAwait(false);

    /// <summary>Map a filesystem fact onto the neutral failure kind. Never carries the path outward.</summary>
    internal static ObjectStoreException Map(string operation, FileSystemFaultException fault)
        => new(fault.Kind switch
        {
            FileSystemFaultKind.NotFound => ObjectStoreFailureKind.MissingObject,
            FileSystemFaultKind.AlreadyExists => ObjectStoreFailureKind.Ambiguous,
            FileSystemFaultKind.PermissionDenied => ObjectStoreFailureKind.Authorization,
            FileSystemFaultKind.NoSpace => ObjectStoreFailureKind.Capacity,
            FileSystemFaultKind.Containment or FileSystemFaultKind.CrossDevice => ObjectStoreFailureKind.Unsupported,
            FileSystemFaultKind.Cancelled => ObjectStoreFailureKind.Canceled,
            _ => ObjectStoreFailureKind.Transient
        }, operation, fault);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A forward-only, length-bounded read view over a committed data file.</summary>
    private sealed class ForwardOnlyReadStream(Stream inner, long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_remaining == 0)
            {
                return 0;
            }
            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0)
            {
                return 0;
            }
            var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        protected override void Dispose(bool disposing)
        {
            // The FileStream is owned by the caller's await-using in ReadAsync; this view owns nothing.
            base.Dispose(disposing);
        }
    }

    private sealed class ReaderCallbackException : Exception
    {
        public ReaderCallbackException()
        {
        }

        public ReaderCallbackException(string message)
            : base(message)
        {
        }

        public ReaderCallbackException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public ReaderCallbackException(Exception innerException)
            : base("reader callback failed", innerException)
        {
        }
    }

    [LoggerMessage(2185, LogLevel.Warning, "Filesystem object store operation {Operation} failed with {Outcome}.")]
    private static partial void LogFailure(ILogger logger, string operation, string outcome, Exception? exception);
}
