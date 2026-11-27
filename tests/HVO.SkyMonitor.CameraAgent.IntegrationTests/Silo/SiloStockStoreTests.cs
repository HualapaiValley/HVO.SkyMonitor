using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Silo;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed partial class SiloStockStoreTests
{
    // Same stock index as the installer, exercised on a new disposable data root.
    private const string StockImage = "docker.io/pgsty/silo@sha256:c20d112390b9a9ce8c11b8af95ad35bcaa62f4759663c09c936cd8d2b511f329";
    private const int Port = 9000;
    private const string Bucket = "cameraagent-versioned-fixture";
    private const string Key = "p/full/fixture";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Integration")]
    public async Task StockSiloEnforcesCreateOnlyWritesAndKeepsExactVersionsAcrossRestart()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var accessKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
        var secretKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var container = BuildStockContainer(accessKey, secretKey, "on");
        await using var containerLease = container.ConfigureAwait(false);
        await container.StartAsync(token).ConfigureAwait(false);
        using var client = SiloS3ObjectStore.CreateClient(Endpoint(container), accessKey, secretKey);
        await CreateVersionedBucketAsync(client, Bucket, token).ConfigureAwait(false);

        var descriptor = new SiloStoreDescriptor("local-silo-fixture", Bucket, SiloStoreClass.Local, SiloRetrieval.Online,
            PersistenceQualified: false);
        var store = new SiloS3ObjectStore(client, descriptor, TimeSpan.FromSeconds(30));
        await CheckStockVersionsAsync(container, client, store, descriptor, accessKey, secretKey, token).ConfigureAwait(false);
    }

    private static IContainer BuildStockContainer(string accessKey, string secretKey, string sync) =>
        new ContainerBuilder(StockImage)
            .WithEnvironment("MINIO_ROOT_USER", accessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", secretKey)
            .WithEnvironment("MINIO_BROWSER", "off")
            .WithEnvironment("MINIO_FS_OSYNC", sync)
            .WithCommand("server", "/data", "--address", ":9000")
            .WithPortBinding(Port, assignRandomHostPort: true)
            .WithCreateParameterModifier(parameters =>
            {
                var bindings = parameters.HostConfig?.PortBindings ?? throw new InvalidOperationException("The fixture requires a loopback binding.");
                foreach (var binding in bindings.Values.SelectMany(static bindings => bindings))
                {
                    binding.HostIP = "127.0.0.1";
                }
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request.ForPort(Port).ForPath("/minio/health/ready")))
            .Build();

    private static Uri Endpoint(IContainer container) => new UriBuilder("http", container.Hostname, container.GetMappedPublicPort(Port)).Uri;

    private static async Task CreateVersionedBucketAsync(AmazonS3Client client, string bucket, CancellationToken token)
    {
        // The health endpoint can answer before the authenticated S3 API accepts requests.
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(token);
        readiness.CancelAfter(TimeSpan.FromSeconds(20));
        while (true)
        {
            try
            {
                await client.ListBucketsAsync(new ListBucketsRequest(), readiness.Token).ConfigureAwait(false);
                break;
            }
            catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.ServiceUnavailable &&
                exception.ErrorCode == "XMinioServerNotInitialized")
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), readiness.Token).ConfigureAwait(false);
            }
        }

        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, token).ConfigureAwait(false);
        await client.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled }
        }, token).ConfigureAwait(false);
    }

    private static async Task CheckStockVersionsAsync(IContainer container, AmazonS3Client client, SiloS3ObjectStore store,
        SiloStoreDescriptor descriptor, string accessKey, string secretKey, CancellationToken token)
    {
        var bytes = RandomNumberGenerator.GetBytes(512 * 1024);
        var write = Put(Key, bytes);
        Assert.IsNull(await store.LocateCurrentVersionAsync(Key, token).ConfigureAwait(false));
        var writes = await Task.WhenAll(store.PutCreateOnlyAsync(write, token).AsTask(), store.PutCreateOnlyAsync(write, token).AsTask())
            .ConfigureAwait(false);
        var firstVersion = writes.Single(static result => result.Created).VersionId!;
        Assert.AreEqual(1, writes.Count(static result => !result.Created));
        Assert.AreEqual(firstVersion, await store.LocateCurrentVersionAsync(Key, token).ConfigureAwait(false));
        await VerifyAsync(store, descriptor, firstVersion, write, token).ConfigureAwait(false);
        var conflictingBytes = new byte[] { 2, 4, 6 };
        Assert.IsFalse((await store.PutCreateOnlyAsync(Put(Key, conflictingBytes), token).ConfigureAwait(false)).Created);
        var rejected = Put("p/full/bad-checksum", conflictingBytes) with { Sha256 = write.Sha256 };
        await Assert.ThrowsExactlyAsync<SiloChecksumRejectedException>(async () =>
            await store.PutCreateOnlyAsync(rejected, token).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.IsNull(await store.LocateCurrentVersionAsync(rejected.Key, token).ConfigureAwait(false));
        var empty = Put("p/full/zero", []);
        var emptyVersion = await store.PutCreateOnlyAsync(empty, token).ConfigureAwait(false);
        Assert.IsTrue(emptyVersion.Created);
        await VerifyAsync(store, descriptor, emptyVersion.VersionId!, empty, token).ConfigureAwait(false);
        var laterKeys = new List<string>();
        await foreach (var key in store.ListKeysAsync("p/full/", Key, token).ConfigureAwait(false))
        {
            laterKeys.Add(key);
        }
        CollectionAssert.AreEqual(new[] { empty.Key }, laterKeys);

        // A foreign write moves the current pointer. An exact read still returns the original bytes.
        using var input = new MemoryStream(conflictingBytes, writable: false);
        var later = await client.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = Key, InputStream = input }, token)
            .ConfigureAwait(false);
        Assert.AreEqual(later.VersionId, await store.LocateCurrentVersionAsync(Key, token).ConfigureAwait(false));
        await container.StopAsync(token).ConfigureAwait(false);
        await container.StartAsync(token).ConfigureAwait(false);

        using var restartedClient = SiloS3ObjectStore.CreateClient(
            new UriBuilder("http", container.Hostname, container.GetMappedPublicPort(Port)).Uri, accessKey, secretKey);
        var restartedStore = new SiloS3ObjectStore(restartedClient, descriptor, TimeSpan.FromSeconds(30));
        await VerifyAsync(restartedStore, descriptor, firstVersion, write, token).ConfigureAwait(false);
        var current = await SiloVerifier.ProbeAsync(restartedStore, new(descriptor.StoreId, Bucket, Key, later.VersionId), write.Length,
            write.Sha256, TimeProvider.System, captureBytes: false, token).ConfigureAwait(false);
        Assert.AreEqual(SiloProbeStatus.Damaged, current.Status);
        var absent = await VerifyMissingVersionAsync(restartedClient, restartedStore, firstVersion, token).ConfigureAwait(false);
        Assert.AreEqual(SiloOpenStatus.NoSuchVersion, absent);
    }

    private static async Task VerifyAsync(
        SiloS3ObjectStore store, SiloStoreDescriptor descriptor, string versionId, SiloPutRequest expected, CancellationToken cancellationToken)
    {
        var proof = await SiloVerifier.ProbeAsync(store, new(descriptor.StoreId, descriptor.Bucket, expected.Key, versionId),
            expected.Length, expected.Sha256, TimeProvider.System, captureBytes: false, cancellationToken).ConfigureAwait(false);
        Assert.AreEqual(SiloProbeStatus.Verified, proof.Status, proof.Detail);
    }

    private static async Task<SiloOpenStatus> VerifyMissingVersionAsync(
        AmazonS3Client client, SiloS3ObjectStore store, string versionId, CancellationToken cancellationToken)
    {
        await client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = Bucket,
            Key = Key,
            VersionId = versionId
        }, cancellationToken).ConfigureAwait(false);
        using var opened = await store.OpenVersionAsync(Key, versionId, cancellationToken).ConfigureAwait(false);
        return opened.Status;
    }

    private static SiloPutRequest Put(string key, byte[] bytes) =>
        new(key, bytes.LongLength, SiloHash.Sha256(bytes), _ => ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false)),
            new Dictionary<string, string>());
}
