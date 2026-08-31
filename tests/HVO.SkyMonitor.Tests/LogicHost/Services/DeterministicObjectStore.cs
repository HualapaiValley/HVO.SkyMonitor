using System.Runtime.CompilerServices;
using HVO.SkyMonitor.LogicHost.Services;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

internal sealed class DeterministicObjectStore : IObjectStore
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _buckets = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Bucket, string Key), StoredObject> _objects = [];
    private readonly Dictionary<string, Queue<ObjectStoreFailureKind>> _failures = new(StringComparer.Ordinal);
    private long _generation;

    public void AddBucket(string bucket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        lock (_gate)
        {
            _buckets.Add(bucket);
        }
    }

    public void FailNext(string operation, ObjectStoreFailureKind kind)
    {
        lock (_gate)
        {
            if (!_failures.TryGetValue(operation, out var failures))
            {
                failures = new Queue<ObjectStoreFailureKind>();
                _failures.Add(operation, failures);
            }
            failures.Enqueue(kind);
        }
    }

    public Task<bool> BucketExistsAsync(string bucket, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfRequested("bucket-exists");
        lock (_gate)
        {
            return Task.FromResult(_buckets.Contains(bucket));
        }
    }

    public async Task PutAsync(
        string bucket,
        string key,
        Stream content,
        long contentLength,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(contentLength, IObjectStore.MaximumPutContentLength);
        EnsureBucket(bucket, "put");
        ThrowIfRequested("put");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)contentLength));
        await content.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _objects[(bucket, key)] = CreateStoredObject(bytes, contentType);
        }
    }

    public Task<ObjectStoreObjectMetadata> StatAsync(
        string bucket,
        string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBucket(bucket, "stat");
        ThrowIfRequested("stat");
        lock (_gate)
        {
            if (!_objects.TryGetValue((bucket, key), out var stored))
            {
                throw new ObjectStoreException(ObjectStoreFailureKind.MissingObject, "stat");
            }
            return Task.FromResult(new ObjectStoreObjectMetadata(
                key,
                stored.Bytes.LongLength,
                stored.ContentType,
                stored.Generation,
                stored.LastModifiedUtc));
        }
    }

    public async Task ReadAsync(
        string bucket,
        string key,
        string? generation,
        Func<Stream, CancellationToken, Task> reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBucket(bucket, "get");
        ThrowIfRequested("get");
        StoredObject stored;
        lock (_gate)
        {
            if (!_objects.TryGetValue((bucket, key), out stored!))
            {
                throw new ObjectStoreException(ObjectStoreFailureKind.MissingObject, "get");
            }
            if (generation is not null && !string.Equals(generation, stored.Generation, StringComparison.Ordinal))
            {
                throw new ObjectStoreException(ObjectStoreFailureKind.Precondition, "get");
            }
        }

        using var content = new MemoryStream(stored.Bytes, writable: false);
        using var forwardOnlyContent = new ForwardOnlyReadStream(content);
        await reader(forwardOnlyContent, cancellationToken).ConfigureAwait(false);
    }

    public Task CopyAsync(
        string bucket,
        string sourceKey,
        string destinationKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBucket(bucket, "copy");
        ThrowIfRequested("copy");
        lock (_gate)
        {
            if (!_objects.TryGetValue((bucket, sourceKey), out var source))
            {
                throw new ObjectStoreException(ObjectStoreFailureKind.MissingObject, "copy");
            }
            _objects[(bucket, destinationKey)] = CreateStoredObject([.. source.Bytes], source.ContentType);
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBucket(bucket, "delete");
        ThrowIfRequested("delete");
        lock (_gate)
        {
            _objects.Remove((bucket, key));
        }
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ObjectStoreItem> ListAsync(
        string bucket,
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        string? startAfter = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureBucket(bucket, "list");
        ThrowIfRequested("list");
        ObjectStoreItem[] items;
        lock (_gate)
        {
            items = _objects
                .Where(pair => pair.Key.Bucket == bucket
                    && pair.Key.Key.StartsWith(prefix, StringComparison.Ordinal)
                    && (startAfter is null || string.CompareOrdinal(pair.Key.Key, startAfter) > 0))
                .OrderBy(pair => pair.Key.Key, StringComparer.Ordinal)
                .Select(pair => new ObjectStoreItem(
                    pair.Key.Key,
                    pair.Value.Bytes.LongLength,
                    pair.Value.LastModifiedUtc))
                .ToArray();
        }
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return item;
        }
    }

    private StoredObject CreateStoredObject(byte[] bytes, string contentType)
    {
        var generation = Interlocked.Increment(ref _generation);
        return new StoredObject(
            bytes,
            contentType,
            $"generation-{generation:D8}",
            DateTimeOffset.UnixEpoch.AddTicks(generation));
    }

    private void EnsureBucket(string bucket, string operation)
    {
        lock (_gate)
        {
            if (!_buckets.Contains(bucket))
            {
                throw new ObjectStoreException(ObjectStoreFailureKind.MissingBucket, operation);
            }
        }
    }

    private void ThrowIfRequested(string operation)
    {
        lock (_gate)
        {
            if (_failures.TryGetValue(operation, out var failures) && failures.TryDequeue(out var kind))
            {
                throw new ObjectStoreException(kind, operation);
            }
        }
    }

    private sealed record StoredObject(
        byte[] Bytes,
        string ContentType,
        string Generation,
        DateTimeOffset LastModifiedUtc);

    private sealed class ForwardOnlyReadStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
