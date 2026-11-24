using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

[TestClass]
public sealed class SiloS3ObjectStoreTests
{
    private static readonly SiloStoreDescriptor Descriptor = new("local-silo", "fixture-bucket",
        SiloStoreClass.Local, SiloRetrieval.Online, PersistenceQualified: false);
    private static readonly SiloExactLocator Locator = new(Descriptor.StoreId, Descriptor.Bucket, "p/example", "version-1");
    private static readonly byte[] Payload = [0, 1, 2, 3];

    [TestMethod]
    [TestCategory("Unit")]
    public async Task BodyInterruptionIsRetryableAndDistinctFromCorruptionAndAbsence()
    {
        var client = new Mock<IAmazonS3>(MockBehavior.Strict);
        client.SetupSequence(s3 => s3.GetObjectAsync(
                It.Is<GetObjectRequest>(request => request.BucketName == Descriptor.Bucket &&
                    request.Key == Locator.Key && request.VersionId == Locator.VersionId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response(new InterruptedBody()))
            .ReturnsAsync(Response(new InterruptedBody()))
            .ReturnsAsync(Response(new MemoryStream([0, 1, 2, 9], writable: false)))
            .ThrowsAsync(new AmazonS3Exception("absent") { ErrorCode = "NoSuchVersion", StatusCode = HttpStatusCode.NotFound })
            .ReturnsAsync(Response(new MemoryStream(Payload, writable: false)));

        var store = Store(client);
        using (var opened = await store.OpenVersionAsync(Locator.Key, Locator.VersionId, TestContext.CancellationToken).ConfigureAwait(false))
        {
            var buffer = new byte[4];
            Assert.AreEqual(2, await opened.Content!.ReadAsync(buffer, TestContext.CancellationToken).ConfigureAwait(false));
            var interrupted = await Assert.ThrowsExactlyAsync<SiloStoreUnavailableException>(() =>
                opened.Content.ReadAsync(buffer, TestContext.CancellationToken).AsTask()).ConfigureAwait(false);
            Assert.IsFalse(interrupted.WriteMayHaveLanded);
        }
        foreach (var expected in new[] { SiloProbeStatus.Unavailable, SiloProbeStatus.Damaged, SiloProbeStatus.Absent, SiloProbeStatus.Verified })
        {
            var result = await SiloVerifier.ProbeAsync(store, Locator, Payload.Length, SiloHash.Sha256(Payload),
                TimeProvider.System, captureBytes: false, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(expected, result.Status);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task ReadTimeoutIsUnavailableAndCallerCancellationPropagates()
    {
        var client = new Mock<IAmazonS3>(MockBehavior.Strict);
        client.Setup(s3 => s3.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response(new StalledBody()));
        var store = new SiloS3ObjectStore(client.Object, Descriptor, TimeSpan.FromMilliseconds(100));
        using var opened = await store.OpenVersionAsync(Locator.Key, Locator.VersionId, TestContext.CancellationToken).ConfigureAwait(false);
        var buffer = new byte[1];
        await Assert.ThrowsExactlyAsync<SiloStoreUnavailableException>(() =>
            opened.Content!.ReadAsync(buffer).AsTask()).ConfigureAwait(false);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            opened.Content!.ReadAsync(buffer, cancelled.Token).AsTask()).ConfigureAwait(false);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task PutUsesCreateOnlyAndSuppliedChecksumAndDistinguishesUncertainWrite()
    {
        var client = new Mock<IAmazonS3>(MockBehavior.Strict);
        var sha = SiloHash.Sha256(Payload);
        client.SetupSequence(s3 => s3.PutObjectAsync(It.Is<PutObjectRequest>(request =>
                request.BucketName == Descriptor.Bucket && request.Key == Locator.Key && request.IfNoneMatch == "*" &&
                request.ChecksumSHA256 == Convert.ToBase64String(Convert.FromHexString(sha)) &&
                request.InputStream.Length == Payload.Length && !request.UseChunkEncoding), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutObjectResponse { VersionId = Locator.VersionId })
            .ThrowsAsync(new AmazonS3Exception("occupied") { StatusCode = HttpStatusCode.PreconditionFailed })
            .ThrowsAsync(new AmazonS3Exception("checksum") { ErrorCode = "BadDigest", StatusCode = HttpStatusCode.BadRequest })
            .ThrowsAsync(new HttpRequestException("connection refused"))
            .ReturnsAsync(new PutObjectResponse { VersionId = "null" });

        var store = Store(client);
        var put = new SiloPutRequest(Locator.Key, Payload.Length, sha,
            _ => ValueTask.FromResult<Stream>(new MemoryStream(Payload, writable: false)), new Dictionary<string, string>());
        var cancellationToken = TestContext.CancellationToken;
        Assert.AreEqual(new SiloPutResult(true, Locator.VersionId), await store.PutCreateOnlyAsync(put, cancellationToken).ConfigureAwait(false));
        Assert.AreEqual(new SiloPutResult(false, null), await store.PutCreateOnlyAsync(put, cancellationToken).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<SiloChecksumRejectedException>(async () =>
            await store.PutCreateOnlyAsync(put, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.ThrowsExactlyAsync<SiloStoreUnavailableException>(async () =>
                await store.PutCreateOnlyAsync(put, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsTrue(error.WriteMayHaveLanded);
        }
        await Assert.ThrowsExactlyAsync<SiloChecksumRejectedException>(async () =>
            await store.PutCreateOnlyAsync(put with { Length = put.Length - 1 }, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () =>
            await store.PutCreateOnlyAsync(put with { Length = SiloS3ObjectStore.MaximumPutBytes + 1 }, cancellationToken)
                .ConfigureAwait(false)).ConfigureAwait(false);
    }

    public TestContext TestContext { get; set; } = null!;

    private static SiloS3ObjectStore Store(Mock<IAmazonS3> client) => new(client.Object, Descriptor, TimeSpan.FromSeconds(5));

    private static GetObjectResponse Response(Stream body) => new() { VersionId = Locator.VersionId, ResponseStream = body };

    private sealed class InterruptedBody() : MemoryStream(new byte[] { 0, 1 }, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position == Length
                ? ValueTask.FromException<int>(new IOException("response body interrupted"))
                : base.ReadAsync(buffer, cancellationToken);
    }

    private sealed class StalledBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }
    }
}
