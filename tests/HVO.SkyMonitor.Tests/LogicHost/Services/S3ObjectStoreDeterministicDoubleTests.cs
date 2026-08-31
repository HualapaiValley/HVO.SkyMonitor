using System.Text;
using HVO.SkyMonitor.LogicHost.Services;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class S3ObjectStoreDeterministicDoubleTests
{
    private const string Bucket = "contract-artifacts";

    [TestMethod]
    public async Task StronglyConsistentOperations_ConformToTheProviderNeutralContract()
    {
        var store = new DeterministicObjectStore();
        store.AddBucket(Bucket);
        var payload = Encoding.UTF8.GetBytes("provider-neutral-object-store");
        using var source = new TrackingForwardOnlyStream(payload);

        await store.PutAsync(
            Bucket,
            "objects/staging.bin",
            source,
            payload.LongLength,
            "application/x-skymonitor-contract",
            CancellationToken.None).ConfigureAwait(false);

        Assert.IsFalse(source.WasDisposed);
        Assert.IsFalse(source.SeekAttempted);
        var staged = await store.StatAsync(Bucket, "objects/staging.bin", CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(payload.LongLength, staged.ContentLength);
        Assert.AreEqual("application/x-skymonitor-contract", staged.ContentType);
        Assert.IsFalse(string.IsNullOrWhiteSpace(staged.Generation));

        var read = new MemoryStream();
        await store.ReadAsync(
            Bucket,
            staged.Key,
            staged.Generation,
            async (content, cancellationToken) =>
            {
                Assert.IsFalse(content.CanSeek);
                await content.CopyToAsync(read, cancellationToken).ConfigureAwait(false);
            },
            CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(payload, read.ToArray());

        var stale = await Assert.ThrowsExactlyAsync<ObjectStoreException>(() => store.ReadAsync(
            Bucket,
            staged.Key,
            staged.Generation + "-stale",
            static (_, _) => Task.CompletedTask,
            CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(ObjectStoreFailureKind.Precondition, stale.Kind);

        await store.CopyAsync(Bucket, staged.Key, "objects/canonical.bin", CancellationToken.None)
            .ConfigureAwait(false);
        var canonical = await store.StatAsync(Bucket, "objects/canonical.bin", CancellationToken.None)
            .ConfigureAwait(false);
        Assert.AreEqual(staged.ContentLength, canonical.ContentLength);
        Assert.AreEqual(staged.ContentType, canonical.ContentType);
        Assert.AreNotEqual(staged.Generation, canonical.Generation);

        foreach (var key in new[] { "objects/003.bin", "objects/001.bin", "objects/002.bin" })
        {
            using var item = new MemoryStream([1]);
            await store.PutAsync(Bucket, key, item, 1, "application/octet-stream", CancellationToken.None)
                .ConfigureAwait(false);
        }
        var listed = new List<ObjectStoreItem>();
        await foreach (var item in store.ListAsync(Bucket, "objects/", CancellationToken.None))
        {
            listed.Add(item);
        }
        CollectionAssert.AreEqual(
            listed.Select(item => item.Key).Order(StringComparer.Ordinal).ToArray(),
            listed.Select(item => item.Key).ToArray());
        var resumed = new List<string>();
        await foreach (var item in store.ListAsync(
            Bucket, "objects/", CancellationToken.None, "objects/002.bin"))
        {
            resumed.Add(item.Key);
        }
        Assert.IsTrue(resumed.All(key => string.CompareOrdinal(key, "objects/002.bin") > 0));
        Assert.AreEqual("objects/003.bin", resumed[0]);

        await store.DeleteAsync(Bucket, staged.Key, CancellationToken.None).ConfigureAwait(false);
        await store.DeleteAsync(Bucket, staged.Key, CancellationToken.None).ConfigureAwait(false);
        var missing = await Assert.ThrowsExactlyAsync<ObjectStoreException>(
            () => store.StatAsync(Bucket, staged.Key, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(ObjectStoreFailureKind.MissingObject, missing.Kind);
    }

    [TestMethod]
    public async Task FailuresAndCancellation_AreDeterministicAndNormalized()
    {
        var store = new DeterministicObjectStore();
        store.AddBucket(Bucket);
        store.FailNext("stat", ObjectStoreFailureKind.Authorization);

        var denied = await Assert.ThrowsExactlyAsync<ObjectStoreException>(
            () => store.StatAsync(Bucket, "missing.bin", CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(ObjectStoreFailureKind.Authorization, denied.Kind);
        Assert.IsFalse(denied.IsRetryable);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in store.ListAsync(Bucket, string.Empty, cancellation.Token))
            {
            }
        }).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Put_RejectsObjectsAboveTheApplicationLimitBeforeReading()
    {
        var store = new DeterministicObjectStore();
        store.AddBucket(Bucket);
        using var content = new TrackingForwardOnlyStream([1]);

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => store.PutAsync(
            Bucket,
            "too-large.bin",
            content,
            IObjectStore.MaximumPutContentLength + 1,
            "application/octet-stream",
            CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(0, content.BytesRead);
    }

    private sealed class TrackingForwardOnlyStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public bool WasDisposed { get; private set; }
        public bool SeekAttempted { get; private set; }
        public long BytesRead { get; private set; }
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
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            BytesRead += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesRead += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            SeekAttempted = true;
            throw new NotSupportedException();
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
