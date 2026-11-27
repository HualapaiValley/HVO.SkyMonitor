using System.Net;
using System.Security.Cryptography;
using System.Globalization;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Silo;

public sealed partial class SiloStockStoreTests
{
    private static readonly string[] RetentionFixtureArtifacts = ["raw-first", "raw-second"];

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("disabled")]
    [DataRow("reachable")]
    [DataRow("offline")]
    [DataRow("full")]
    public async Task StockSiloRetentionSerializesSharedReferencesLeasesAndArchiveSweep(string archiveMode)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        var key = ArchiveKeys();
        var source = BuildCappedRetentionStore(key.Access, key.Secret);
        await using var sourceLifetime = source.ConfigureAwait(false);
        await source.StartAsync(token).ConfigureAwait(false);
        using var sourceClient = SiloS3ObjectStore.CreateClient(Endpoint(source), key.Access, key.Secret);
        await CreateVersionedBucketAsync(sourceClient, Bucket, token).ConfigureAwait(false);
        var binding = new SiloStorageBinding("fixture-instance", "fixture-local", Bucket);
        var local = new SiloS3ObjectStore(sourceClient, new(binding.StoreId, binding.Bucket, SiloStoreClass.Local,
            SiloRetrieval.Online, PersistenceQualified: true), TimeSpan.FromSeconds(10));

        using var directory = new ArchiveTestDirectory();
        using var root = SiloStateRoot.Acquire(directory.Path);
        var clock = new ArchiveTestClock();
        var physical = await ReadPhysicalHeadroomAsync(source, token).ConfigureAwait(false);
        var budget = new SiloDiskBudget(() => physical, 1, 1, 1, 1);
        var bytes = RandomNumberGenerator.GetBytes(1024 * 1024);
        var digest = SiloHash.Sha256(bytes);
        var states = await PublishRetentionFixtureAsync(root, binding, local, clock, budget, bytes, token).ConfigureAwait(false);
        physical = await ReadPhysicalHeadroomAsync(source, token).ConfigureAwait(false);
        var (locator, first, second) = states;
        if (archiveMode != "disabled")
        {
            var archiveKeys = ArchiveKeys();
            var archive = BuildCappedRetentionStore(archiveKeys.Access, archiveKeys.Secret);
            await using var archiveLifetime = archive.ConfigureAwait(false);
            await archive.StartAsync(token).ConfigureAwait(false);
            using var archiveClient = SiloS3ObjectStore.CreateClient(Endpoint(archive), archiveKeys.Access, archiveKeys.Secret);
            const string archiveBucket = "capped-retention-archive";
            await CreateArchiveBucketAsync(archiveClient, archiveBucket, token).ConfigureAwait(false);
            var archiveStore = new SiloS3ObjectStore(archiveClient, new("fixture-archive", archiveBucket, SiloStoreClass.Archive,
                SiloRetrieval.Online, PersistenceQualified: true), TimeSpan.FromSeconds(10));
            var custody = new StockRetentionCustody(archiveClient, archiveStore, clock);
            var survivor = await ArchiveRetentionFixtureAsync(archiveMode, archive, archiveStore, locator, bytes, token).ConfigureAwait(false);
            await SweepRetentionFixtureAsync(root, binding, local, clock, budget, states, archiveStore, survivor, custody, token)
                .ConfigureAwait(false);
        }
        else
        {
            await SweepRetentionFixtureAsync(root, binding, local, clock, budget, states, null, null, null, token).ConfigureAwait(false);
        }
        // Stock Silo moved the deleted version to trash. A second sweep cannot assume the returned DELETE reclaimed blocks.
        var afterDelete = await ReadPhysicalHeadroomAsync(source, token).ConfigureAwait(false);
        Assert.AreEqual(physical.TotalBytes, afterDelete.TotalBytes);
        Assert.IsGreaterThan(physical.TrashBytes!.Value, afterDelete.TrashBytes!.Value);
        physical = afterDelete;
        await AssertReclaimPendingAfterRestartAsync(root, binding, local, clock, budget, token).ConfigureAwait(false);

        var authority = new SiloPublicationState(root.PublicationDatabasePath, binding, clock);
        var writer = await authority.ReadWriterEvidenceAsync(token).ConfigureAwait(false);
        await SiloCatalog.RebuildAsync(root, binding, new SiloStoreSet(local), SiloNoAuthorityEvidence.Instance,
            SiloNoAuthorityEvidence.Instance, clock, new(local, Writer: writer), token).ConfigureAwait(false);
        using var rebuilt = new SiloCatalogIndex(root.IndexDatabasePath, binding, clock);
        foreach (var identity in new[] { first, second })
        {
            var role = await rebuilt.ReadRoleAsync(identity.Role, token).ConfigureAwait(false);
            Assert.AreEqual("evicted", role?.Locators.Single(row => row.Locator == locator).CopyState);
            Assert.AreEqual(archiveMode == "reachable" ? null : "policy-expired", role?.Journaled);
        }
        Assert.AreEqual(digest, first.Sha256);
    }

    private sealed record RetentionFixture(SiloExactLocator Locator, SiloObjectIdentity First, SiloObjectIdentity Second);

    private static async ValueTask<SiloPhysicalHeadroom> ReadPhysicalHeadroomAsync(IContainer store, CancellationToken token)
    {
        var capacity = await store.ExecAsync(["df", "-kP", "/data"], token).ConfigureAwait(false);
        Assert.AreEqual(0L, capacity.ExitCode);
        var columns = capacity.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var trash = await store.ExecAsync(["du", "-sk", "/data/.minio.sys/tmp/.trash"], token).ConfigureAwait(false);
        Assert.AreEqual(0L, trash.ExitCode);
        return new(ParseKiB(columns[1]), ParseKiB(columns[3]), ParseKiB(trash.Stdout.Split('\t')[0]));

        static long ParseKiB(string value) => checked(long.Parse(value, CultureInfo.InvariantCulture) * 1024L);
    }

    private static IContainer BuildCappedRetentionStore(string accessKey, string secretKey) =>
        new ContainerBuilder(StockImage)
            .WithEnvironment("MINIO_ROOT_USER", accessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", secretKey)
            .WithEnvironment("MINIO_BROWSER", "off")
            .WithEnvironment("MINIO_FS_OSYNC", "on")
            .WithCommand("server", "/data", "--address", ":9000")
            .WithPortBinding(Port, assignRandomHostPort: true)
            .WithCreateParameterModifier(parameters =>
            {
                var host = parameters.HostConfig ?? throw new InvalidOperationException("The retention fixture requires a capped mount.");
                var bindings = host.PortBindings ?? throw new InvalidOperationException("The retention fixture requires a loopback binding.");
                host.Tmpfs = new Dictionary<string, string> { ["/data"] = "rw,size=96m" };
                foreach (var binding in bindings.Values.SelectMany(static ports => ports))
                    binding.HostIP = "127.0.0.1";
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                request => request.ForPort(Port).ForPath("/minio/health/ready")))
            .Build();

    private static async ValueTask<SiloExactLocator?> ArchiveRetentionFixtureAsync(string mode, IContainer archive,
        SiloS3ObjectStore archiveStore, SiloExactLocator source, byte[] bytes, CancellationToken token)
    {
        if (mode == "offline")
        {
            await archive.StopAsync(token).ConfigureAwait(false);
        }
        else if (mode == "full")
        {
            // Fill this test container's capped filesystem. It has no persistent or host bind mount.
            await archive.ExecAsync(["dd", "if=/dev/zero", "of=/data/cap-fixture", "bs=1048576", "count=96"], token).ConfigureAwait(false);
        }
        if (mode != "reachable")
        {
            await Assert.ThrowsExactlyAsync<SiloStoreUnavailableException>(() =>
                archiveStore.PutCreateOnlyAsync(Put(source.Key, bytes), token).AsTask()).ConfigureAwait(false);
            return null;
        }
        var written = await archiveStore.PutCreateOnlyAsync(Put(source.Key, bytes), token).ConfigureAwait(false);
        return new(archiveStore.Descriptor.StoreId, archiveStore.Descriptor.Bucket, source.Key, written.VersionId!);
    }

    private static async ValueTask AssertReclaimPendingAfterRestartAsync(SiloStateRoot root, SiloStorageBinding binding,
        ISiloObjectStore local, TimeProvider clock, ISiloDiskBudget budget, CancellationToken token)
    {
        var state = new SiloPublicationState(root.PublicationDatabasePath, binding, clock);
        using var index = new SiloCatalogIndex(root.IndexDatabasePath, binding, clock);
        using var journal = new SiloJournalWriter(state, root.Spool, local, binding, clock);
        var catalog = new SiloCatalog(index, new SiloStoreSet(local), SiloNoAuthorityEvidence.Instance);
        using var publisher = new SiloPublisher(state, root.Spool, journal, catalog, new SiloFilePayloadSpool(root, 8 * 1024 * 1024, 32), binding, clock);
        var retention = new SiloRetention(state, index, publisher, catalog, journal, root.Root, local, budget, clock);
        Assert.AreEqual("awaiting-physical-reclamation", await retention.SweepAsync(true,
            _ => ValueTask.FromResult<IReadOnlyList<SiloRetentionHold>>([]), null, token).ConfigureAwait(false));
    }

    private static async ValueTask<RetentionFixture> PublishRetentionFixtureAsync(SiloStateRoot root, SiloStorageBinding binding,
        ISiloObjectStore local, TimeProvider clock, ISiloDiskBudget budget, byte[] bytes, CancellationToken token)
    {
        var state = new SiloPublicationState(root.PublicationDatabasePath, binding, clock);
        await state.InitializeAsync(token).ConfigureAwait(false);
        using var index = new SiloCatalogIndex(root.IndexDatabasePath, binding, clock);
        await index.InitializeAsync(token).ConfigureAwait(false);
        using var journal = new SiloJournalWriter(state, root.Spool, local, binding, clock);
        await journal.OpenFirstGenerationAsync(new Dictionary<string, long>(), token).ConfigureAwait(false);
        var catalog = new SiloCatalog(index, new SiloStoreSet(local), SiloNoAuthorityEvidence.Instance);
        var spool = new SiloFilePayloadSpool(root, 8 * 1024 * 1024, 32);
        using var publisher = new SiloPublisher(state, root.Spool, journal, catalog, spool, binding, clock);
        var retention = new SiloRetention(state, index, publisher, catalog, journal, root.Root, local, budget, clock);
        var hash = SiloHash.Sha256(bytes);
        var now = clock.GetUtcNow();
        var members = RetentionFixtureArtifacts.Select(id => new SiloDraftMember(id, "r1", "raw",
            SiloRetentionClass.Full, "application/octet-stream", bytes.Length, hash, false, SiloFilePayloadSpool.TokenOf(hash))).ToArray();
        foreach (var member in members)
        {
            var image = new SiloGroupDraft("retention-fixture", member.ArtifactId, "r1", now, now.AddSeconds(-1), now,
                [member], [], new([]));
            await publisher.StageAsync(image, (_, cancellation) => SiloArtifactStorage.OpenMemoryAsync(bytes, cancellation), token)
                .ConfigureAwait(false);
            Assert.AreEqual(SiloStagedState.Visible, (await publisher.PublishAsync(image.GroupId, image.RevisionId, token).ConfigureAwait(false)).State);
        }
        await retention.SetPolicyAsync(new(false, [new("raw", TimeSpan.Zero, true, true)]), token).ConfigureAwait(false);
        var pending = (await retention.DryRunAsync(now, false, [], token).ConfigureAwait(false))
            .Single(static item => item.Locator.Key.StartsWith("p/full/", StringComparison.Ordinal));
        Assert.HasCount(2, pending.References);
        Assert.IsNotNull(await retention.TryAcquireAsync([pending.Locator], TimeSpan.FromMinutes(2), token).ConfigureAwait(false));
        return new(pending.Locator, pending.References[0].Identity, pending.References[1].Identity);
    }

    private static async ValueTask SweepRetentionFixtureAsync(SiloStateRoot root, SiloStorageBinding binding, ISiloObjectStore local,
        TimeProvider clock, ISiloDiskBudget budget, RetentionFixture fixture, ISiloObjectStore? alternate, SiloExactLocator? survivor,
        StockRetentionCustody? custody, CancellationToken token)
    {
        var state = new SiloPublicationState(root.PublicationDatabasePath, binding, clock);
        using var index = new SiloCatalogIndex(root.IndexDatabasePath, binding, clock);
        using var journal = new SiloJournalWriter(state, root.Spool, local, binding, clock);
        var catalog = new SiloCatalog(index, new SiloStoreSet(local, alternate is null ? [] : [alternate]), SiloNoAuthorityEvidence.Instance);
        using var publisher = new SiloPublisher(state, root.Spool, journal, catalog, new SiloFilePayloadSpool(root, 8 * 1024 * 1024, 32), binding, clock);
        var retention = new SiloRetention(state, index, publisher, catalog, journal, root.Root, local, budget, clock);
        if (survivor is not null)
        {
            await retention.SetPolicyAsync(new(false, [new("raw", TimeSpan.Zero, true, false)]), token).ConfigureAwait(false);
            await journal.AppendAsync(new[] { fixture.First, fixture.Second }.Select(identity => (SiloJournalRecord)new SiloCopyRecord(
                identity, survivor, SiloStoreClass.Archive, SiloRetrieval.Online, "archive-owner", clock.GetUtcNow(),
                SiloFormats.ExactGetSha256)).ToArray(), "stock-retention-copies", token).ConfigureAwait(false);
        }
        static ValueTask<IReadOnlyList<SiloRetentionHold>> ReadHolds(CancellationToken _) =>
            ValueTask.FromResult<IReadOnlyList<SiloRetentionHold>>([]);
        var lease = SiloRetentionState.Read(root.Root).Leases.Single();
        Assert.IsTrue(await retention.RevalidateAsync(lease, token).ConfigureAwait(false));
        var restore = SiloVerifier.ProbeAsync(local, fixture.Locator, fixture.First.Length, fixture.First.Sha256,
            clock, captureBytes: false, token).AsTask();
        Assert.AreEqual("no-eligible-versions", await retention.SweepAsync(true, ReadHolds, custody, token).ConfigureAwait(false));
        Assert.AreEqual(SiloProbeStatus.Verified, (await restore.ConfigureAwait(false)).Status);
        await retention.ReleaseAsync(lease, token).ConfigureAwait(false);
        if (survivor is not null)
            Assert.AreEqual("no-eligible-versions", await retention.SweepAsync(true, ReadHolds, null, token).ConfigureAwait(false));
        Assert.AreEqual("deleted", await retention.SweepAsync(true, ReadHolds, custody, token).ConfigureAwait(false));
        Assert.AreEqual("awaiting-physical-reclamation", await retention.SweepAsync(true, ReadHolds, custody, token).ConfigureAwait(false));
        if (survivor is not null) Assert.IsTrue(custody!.CompetingSweepWasRefused);
    }

    private sealed class StockRetentionCustody(AmazonS3Client client, SiloS3ObjectStore store, TimeProvider clock) : ISiloRetentionArchive
    {
        public bool CompetingSweepWasRefused { get; private set; }

        public async ValueTask<bool> ProtectAsync(SiloObjectIdentity identity, SiloExactLocator survivor, CancellationToken cancellationToken)
        {
            await SetArchiveHoldAsync(client, survivor.Bucket, survivor, ObjectLockLegalHoldStatus.On, cancellationToken).ConfigureAwait(false);
            return await ConfirmProtectedAsync(identity, survivor, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<bool> ConfirmProtectedAsync(SiloObjectIdentity identity, SiloExactLocator survivor, CancellationToken cancellationToken)
        {
            try
            {
                await client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = survivor.Bucket,
                    Key = survivor.Key,
                    VersionId = survivor.VersionId
                }, cancellationToken).ConfigureAwait(false);
                return false;
            }
            catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.BadRequest &&
                exception.ErrorCode == "InvalidRequest")
            {
                CompetingSweepWasRefused = true;
            }
            return (await SiloVerifier.ProbeAsync(store, survivor, identity.Length, identity.Sha256, clock, false, cancellationToken)
                .ConfigureAwait(false)).Status == SiloProbeStatus.Verified;
        }
    }
}
