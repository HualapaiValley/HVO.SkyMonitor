using System.Security.Cryptography;
using HVO.SkyMonitor.LogicHost.Services;

namespace HVO.SkyMonitor.IntegrationTests;

internal enum ObjectStoreConformanceFault
{
    Authentication,
    Authorization,
    Throttled,
    Timeout,
    Unavailable,
    AmbiguousDelete
}

/// <summary>
/// Which injectable faults a provider harness can produce. Every provider must classify the
/// universal outcomes (missing object, missing bucket, caller cancellation, caller-callback
/// failure); only a networked transport can be made to authenticate badly, throttle, time
/// out at the wire, or lose a response after a delete. A filesystem harness declares none
/// of these, and the suite skips exactly those assertions rather than the whole test, so a
/// provider cannot pass by declaring nothing while still being held to the universal set.
/// </summary>
[Flags]
internal enum ObjectStoreConformanceCapabilities
{
    None = 0,
    InjectAuthentication = 1 << 0,
    InjectAuthorization = 1 << 1,
    InjectThrottled = 1 << 2,
    InjectTimeout = 1 << 3,
    InjectUnavailable = 1 << 4,
    InjectAmbiguousDelete = 1 << 5,
    NetworkedTransport = InjectAuthentication | InjectAuthorization | InjectThrottled
        | InjectTimeout | InjectUnavailable | InjectAmbiguousDelete
}

/// <summary>
/// The universal <c>IObjectStore</c> contract every provider must satisfy: streaming
/// publication at the maximum size, conditional read by generation, ordinal complete listing
/// across provider pages, universal failure classification, caller-failure preservation, and
/// (where the transport can lose a response) ambiguous-delete convergence. A provider's
/// conformance test class constructs one of these with its own store, its own fault factory
/// for the faults it declares, and runs each method. Provider-specific behaviour (S3 request
/// signing, region and addressing configuration, SDK exception mapping) is tested beside the
/// provider, not here.
/// </summary>
internal sealed class ObjectStoreConformanceSuite(
    IObjectStore store,
    Func<ObjectStoreConformanceFault, IObjectStore> createFaultedStore,
    string bucket,
    string prefix,
    ObjectStoreConformanceCapabilities capabilities = ObjectStoreConformanceCapabilities.NetworkedTransport)
{
    public async Task CleanupAsync()
    {
        var keys = new List<string>();
        await foreach (var item in store.ListAsync(bucket, prefix, CancellationToken.None))
        {
            keys.Add(item.Key);
        }
        await Parallel.ForEachAsync(
            keys,
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (key, cancellationToken) =>
                await store.DeleteAsync(bucket, key, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }

    public async Task MaximumStreamingPublicationConditionalReadAndDeleteAsync()
    {
        var stagingKey = prefix + "staging/payload.bin";
        var canonicalKey = prefix + "canonical/payload.bin";
        using var source = new PatternReadStream(IObjectStore.MaximumPutContentLength);

        await store.PutAsync(
            bucket,
            stagingKey,
            source,
            IObjectStore.MaximumPutContentLength,
            "application/x-skymonitor-contract",
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(source.WasDisposed);
        Assert.IsFalse(source.UnsupportedMemberAccessed);
        Assert.AreEqual(IObjectStore.MaximumPutContentLength, source.BytesRead);
        var expectedSha256 = source.GetSha256();
        var staged = await store.StatAsync(bucket, stagingKey, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(IObjectStore.MaximumPutContentLength, staged.ContentLength);
        Assert.AreEqual("application/x-skymonitor-contract", staged.ContentType);
        Assert.IsFalse(string.IsNullOrWhiteSpace(staged.Generation));
        Assert.AreEqual(TimeSpan.Zero, staged.LastModifiedUtc.Offset);
        CollectionAssert.AreEqual(expectedSha256, await ReadSha256Async(stagingKey, staged.Generation).ConfigureAwait(false));

        var stale = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => store.ReadAsync(
            bucket,
            stagingKey,
            staged.Generation + "-stale",
            static (_, _) => Task.CompletedTask,
            CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(ObjectStoreFailureKind.Precondition, stale.Kind);

        await store.CopyAsync(bucket, stagingKey, canonicalKey, CancellationToken.None).ConfigureAwait(false);
        var canonical = await store.StatAsync(bucket, canonicalKey, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(staged.ContentLength, canonical.ContentLength);
        Assert.AreEqual(staged.ContentType, canonical.ContentType);
        Assert.AreEqual(TimeSpan.Zero, canonical.LastModifiedUtc.Offset);
        CollectionAssert.AreEqual(
            expectedSha256,
            await ReadSha256Async(canonicalKey, canonical.Generation).ConfigureAwait(false));

        await store.DeleteAsync(bucket, stagingKey, CancellationToken.None).ConfigureAwait(false);
        await store.DeleteAsync(bucket, stagingKey, CancellationToken.None).ConfigureAwait(false);
        var missing = await Assert.ThrowsExactlyAsync<ObjectStoreException>(
            () => store.StatAsync(bucket, stagingKey, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(ObjectStoreFailureKind.MissingObject, missing.Kind);
    }

    public async Task ListingIsCompleteOrdinalAndCrossesProviderPagesAsync()
    {
        const int objectCount = 1005;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, objectCount).Reverse(),
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            async (index, cancellationToken) =>
            {
                using var content = new MemoryStream([(byte)(index % 251)], writable: false);
                await store.PutAsync(
                    bucket,
                    $"{prefix}pages/{index:D4}.bin",
                    content,
                    1,
                    "application/octet-stream",
                    cancellationToken).ConfigureAwait(false);
            }).ConfigureAwait(false);

        var listed = new List<ObjectStoreItem>();
        await foreach (var item in store.ListAsync(bucket, prefix + "pages/", CancellationToken.None))
        {
            listed.Add(item);
        }

        Assert.AreEqual(objectCount, listed.Count);
        CollectionAssert.AreEqual(
            listed.Select(item => item.Key).Order(StringComparer.Ordinal).ToArray(),
            listed.Select(item => item.Key).ToArray());
        Assert.IsTrue(listed.All(item => item.ContentLength == 1));
        Assert.IsTrue(listed.All(item => item.LastModifiedUtc.Offset == TimeSpan.Zero));
    }

    public async Task FailuresAreClassifiedAndCallerFailuresArePreservedAsync()
    {
        var missing = await Assert.ThrowsExactlyAsync<ObjectStoreException>(
            () => store.StatAsync(bucket, prefix + "missing.bin", CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(ObjectStoreFailureKind.MissingObject, missing.Kind);

        var missingBucket = "issue-504-missing-" + Guid.NewGuid().ToString("N");
        Assert.IsFalse(await store.BucketExistsAsync(missingBucket, CancellationToken.None).ConfigureAwait(false));
        var bucketFailure = await Assert.ThrowsExactlyAsync<ObjectStoreException>(
            () => store.StatAsync(missingBucket, "missing.bin", CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(ObjectStoreFailureKind.MissingBucket, bucketFailure.Kind);

        if (capabilities.HasFlag(ObjectStoreConformanceCapabilities.InjectAuthentication))
        {
            await AssertFailureKindAsync(ObjectStoreConformanceFault.Authentication, ObjectStoreFailureKind.Authentication)
                .ConfigureAwait(false);
        }
        if (capabilities.HasFlag(ObjectStoreConformanceCapabilities.InjectAuthorization))
        {
            await AssertFailureKindAsync(ObjectStoreConformanceFault.Authorization, ObjectStoreFailureKind.Authorization)
                .ConfigureAwait(false);
        }
        if (capabilities.HasFlag(ObjectStoreConformanceCapabilities.InjectThrottled))
        {
            await AssertFailureKindAsync(ObjectStoreConformanceFault.Throttled, ObjectStoreFailureKind.Throttled)
                .ConfigureAwait(false);
        }
        if (capabilities.HasFlag(ObjectStoreConformanceCapabilities.InjectTimeout))
        {
            await AssertFailureKindAsync(ObjectStoreConformanceFault.Timeout, ObjectStoreFailureKind.Timeout)
                .ConfigureAwait(false);
        }
        if (capabilities.HasFlag(ObjectStoreConformanceCapabilities.InjectUnavailable))
        {
            await AssertFailureKindAsync(ObjectStoreConformanceFault.Unavailable, ObjectStoreFailureKind.Transient)
                .ConfigureAwait(false);
        }

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => store.StatAsync(bucket, prefix + "canceled.bin", cancellation.Token)).ConfigureAwait(false);

        var callbackKey = prefix + "callback.bin";
        using (var content = new MemoryStream([1, 2, 3], writable: false))
        {
            await store.PutAsync(
                bucket,
                callbackKey,
                content,
                content.Length,
                "application/octet-stream",
                CancellationToken.None).ConfigureAwait(false);
        }
        var callbackFailure = new InvalidOperationException("caller callback failure");
        var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.ReadAsync(
            bucket,
            callbackKey,
            null,
            (_, _) => Task.FromException(callbackFailure),
            CancellationToken.None)).ConfigureAwait(false);
        Assert.AreSame(callbackFailure, observed);
    }

    public async Task AmbiguousDeleteConvergesOnRetryAsync()
    {
        if (!capabilities.HasFlag(ObjectStoreConformanceCapabilities.InjectAmbiguousDelete))
        {
            Assert.Inconclusive("This provider's transport cannot lose a response after a delete; ambiguity is not a reachable state.");
        }

        var key = prefix + "ambiguous-delete.bin";
        using (var content = new MemoryStream([42], writable: false))
        {
            await store.PutAsync(
                bucket,
                key,
                content,
                content.Length,
                "application/octet-stream",
                CancellationToken.None).ConfigureAwait(false);
        }

        var faultedStore = createFaultedStore(ObjectStoreConformanceFault.AmbiguousDelete);
        var ambiguous = await Assert.ThrowsExactlyAsync<ObjectStoreException>(
            () => faultedStore.DeleteAsync(bucket, key, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(ObjectStoreFailureKind.Ambiguous, ambiguous.Kind);
        await faultedStore.DeleteAsync(bucket, key, CancellationToken.None).ConfigureAwait(false);
        var missing = await Assert.ThrowsExactlyAsync<ObjectStoreException>(
            () => store.StatAsync(bucket, key, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(ObjectStoreFailureKind.MissingObject, missing.Kind);
    }

    private async Task AssertFailureKindAsync(
        ObjectStoreConformanceFault fault,
        ObjectStoreFailureKind expected)
    {
        var exception = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() =>
            createFaultedStore(fault).BucketExistsAsync(bucket, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(expected, exception.Kind);
    }

    private async Task<byte[]> ReadSha256Async(string key, string generation)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytesRead = 0;
        await store.ReadAsync(
            bucket,
            key,
            generation,
            async (content, cancellationToken) =>
            {
                Assert.IsFalse(content.CanSeek);
                var buffer = GC.AllocateUninitializedArray<byte>(128 * 1024);
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer.AsSpan(0, read));
                    bytesRead += read;
                }
            },
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(IObjectStore.MaximumPutContentLength, bytesRead);
        return hash.GetHashAndReset();
    }

    private sealed class PatternReadStream(long length) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _position;
        private byte[]? _sha256;

        public long BytesRead => _position;
        public bool UnsupportedMemberAccessed { get; private set; }
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length
        {
            get
            {
                UnsupportedMemberAccessed = true;
                throw new NotSupportedException();
            }
        }
        public override long Position
        {
            get
            {
                UnsupportedMemberAccessed = true;
                throw new NotSupportedException();
            }
            set
            {
                UnsupportedMemberAccessed = true;
                throw new NotSupportedException();
            }
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, length - _position);
            for (var index = 0; index < count; index++)
            {
                buffer[index] = (byte)(((_position + index) * 31 + 17) % 251);
            }
            if (count > 0)
            {
                _hash.AppendData(buffer[..count]);
                _position += count;
            }
            return count;
        }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
        public byte[] GetSha256() => _sha256 ??= _hash.GetHashAndReset();
        public override long Seek(long offset, SeekOrigin origin)
        {
            UnsupportedMemberAccessed = true;
            throw new NotSupportedException();
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            if (disposing)
            {
                _hash.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
