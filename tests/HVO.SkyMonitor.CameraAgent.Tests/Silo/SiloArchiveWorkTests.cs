using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.Silo.S3;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Silo")]
public sealed class SiloArchiveWorkTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task VerifiedCutoffStopsAtUnverifiedReceiptsAndCrossesSettledExpiry()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var index = new SiloArchiveIndex(Path.Combine(silo.RootPath, "archive.db"), Marker(silo.Binding));
        await index.InitializeAsync(token).ConfigureAwait(false);
        for (var number = 1; number <= 3; number++)
            await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, number)).ConfigureAwait(false);
        await index.DiscoverAsync(silo.Index, 10, token).ConfigureAwait(false);
        var now = silo.Clock.GetUtcNow();
        var work = await index.ReadDueAsync(now, 10, token).ConfigureAwait(false);
        Assert.HasCount(3, work);
        var first = new SiloCommittedSegment(new(1, 5), new string('a', 64), "receipt-1");
        var second = new SiloCommittedSegment(new(1, 6), new string('b', 64), "receipt-2");
        var third = new SiloCommittedSegment(new(1, 7), new string('c', 64), "receipt-3");
        await index.CompleteAttemptAsync(work[0], "reachable", null, now, first, now, token).ConfigureAwait(false);
        await index.CompleteAttemptAsync(work[2], "reachable", null, now, third, now, token).ConfigureAwait(false);
        await index.SavePrefixAsync(7, third.Sha256, token).ConfigureAwait(false);
        Assert.AreEqual(work[0].Publication.GroupId, (await index.ReadBacklogAsync(token).ConfigureAwait(false)).Cutoff?.GroupId);
        await index.CompleteAttemptAsync(work[0], "policy-expired", "archive-copy-policy-expired", now, null, now, token).ConfigureAwait(false);
        Assert.IsNull((await index.ReadBacklogAsync(token).ConfigureAwait(false)).Cutoff);
        await index.CompleteAttemptAsync(work[1], "reachable", null, now, second, now, token).ConfigureAwait(false);

        var caughtUp = await index.ReadBacklogAsync(token).ConfigureAwait(false);
        Assert.AreEqual(0L, caughtUp.Count);
        Assert.AreEqual(work[2].Publication.GroupId, caughtUp.Cutoff?.GroupId);
        var expired = await index.FindAsync(work[0].Publication.GroupId, work[0].Publication.RevisionId, token).ConfigureAwait(false);
        Assert.AreEqual("policy-expired", expired?.State);
        Assert.AreEqual(first.VersionId, expired?.Receipt?.ReceiptVersionId);
        Assert.IsFalse((await index.ReadDueAsync(now, 10, token).ConfigureAwait(false)).Any(row => row.Publication == work[0].Publication));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OpeningArchiveIndexRefusesAndPreservesForeignOrNewerWal(bool newer)
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var path = Path.Combine(silo.RootPath, "archive.db");
        var index = new SiloArchiveIndex(path, Marker(silo.Binding));
        await index.InitializeAsync(token).ConfigureAwait(false);

        using (var writer = await SiloSqlite.OpenAsync(path, token).ConfigureAwait(false))
        {
            await SiloSqlite.ExecuteAsync(writer, null, "PRAGMA wal_autocheckpoint = 0;", token).ConfigureAwait(false);
            if (newer)
                await SiloSqlite.ExecuteAsync(writer, null, "PRAGMA user_version = 2;", token).ConfigureAwait(false);
            else
                await SiloSqlite.ExecuteAsync(writer, null, "UPDATE silo_meta SET value = 'foreign' WHERE key = 'sourceBinding';", token)
                    .ConfigureAwait(false);
            File.Copy(path, path + ".snapshot");
            File.Copy(path + "-wal", path + ".snapshot-wal");
        }
        File.Delete(path + "-shm");
        File.Move(path + ".snapshot", path, overwrite: true);
        File.Move(path + ".snapshot-wal", path + "-wal", overwrite: true);
        var before = await FingerprintAsync(path, token).ConfigureAwait(false);

        if (newer)
            await Assert.ThrowsExactlyAsync<SiloFormatRefusedException>(() => index.InitializeAsync(token).AsTask()).ConfigureAwait(false);
        else
            await Assert.ThrowsExactlyAsync<SiloConflictException>(() => index.InitializeAsync(token).AsTask()).ConfigureAwait(false);

        Assert.AreEqual(before, await FingerprintAsync(path, token).ConfigureAwait(false));
        Assert.IsFalse(File.Exists(path + "-shm"), "Inspection must not open the original database.");
    }

    [TestMethod]
    public async Task ArchiveIndexRefusesForeignFormatAndBinding()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var path = Path.Combine(silo.RootPath, "archive.db");
        var marker = Marker(silo.Binding);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            await SiloSqlite.ExecuteAsync(connection, null, "CREATE TABLE foreign_table(value TEXT);", token).ConfigureAwait(false);
        }
        await Assert.ThrowsExactlyAsync<SiloFormatRefusedException>(() => new SiloArchiveIndex(path, marker).InitializeAsync(token).AsTask())
            .ConfigureAwait(false);
        File.Delete(path);
        await new SiloArchiveIndex(path, marker).InitializeAsync(token).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SiloConflictException>(() => new SiloArchiveIndex(path, marker with { Bucket = "other" })
            .InitializeAsync(token).AsTask()).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DisabledWithoutCredentialsAndUnverifiedBindingPreserveTheCorrectStatus()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var (settings, store) = LocalStore();
        var root = Path.Combine(silo.RootPath, "installed");
        using var installed = OpenLocal(settings, store, root, silo.Clock);
        using var services = new ServiceCollection().AddSingleton(installed).BuildServiceProvider();
        var config = new ConfigurationManager();
        using var worker = new SiloArchiveWorker(config, services, silo.Clock, NullLogger<SiloArchiveWorker>.Instance);
        Assert.AreEqual("disabled", (await worker.RunCycleAsync(token).ConfigureAwait(false)).State);
        var section = config.GetSection(SiloArchiveFixture.SettingsPath);
        section["ReplicationEnabled"] = "false";
        Assert.AreEqual("disabled", (await worker.RunCycleAsync(token).ConfigureAwait(false)).State);

        var marker = Marker(installed.Binding);
        var ownCatalog = new SiloArchiveIndex(installed.PhysicalState.Resolve(marker.StoreId + ".db"), marker);
        await ownCatalog.InitializeAsync(token).ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1)).ConfigureAwait(false);
        await ownCatalog.DiscoverAsync(silo.Index, 10, token).ConfigureAwait(false);
        var now = silo.Clock.GetUtcNow();
        var work = (await ownCatalog.ReadDueAsync(now, 10, token).ConfigureAwait(false)).Single();
        var commit = new SiloCommittedSegment(new(1, 3), new string('e', 64), "persisted-receipt");
        await ownCatalog.CompleteAttemptAsync(work, "reachable", null, now, commit, now, token).ConfigureAwait(false);
        await ownCatalog.SavePrefixAsync(commit.Position.Sequence, commit.Sha256, token).ConfigureAwait(false);

        section["StoreId"] = marker.StoreId;
        section["Bucket"] = marker.Bucket;
        section["SourceBindingId"] = marker.SourceBindingId;
        Assert.IsFalse(installed.BindingVerified);
        var disabled = await worker.RunCycleAsync(token).ConfigureAwait(false);
        Assert.AreEqual("disabled", disabled.State);
        Assert.AreEqual(commit.VersionId, disabled.VerifiedCutoff?.ReceiptVersionId);

        section["ReplicationEnabled"] = "true";
        using var restarted = new SiloArchiveWorker(config, services, silo.Clock, NullLogger<SiloArchiveWorker>.Instance);
        var pending = await restarted.RunCycleAsync(token).ConfigureAwait(false);
        Assert.AreEqual("pending", pending.State);
        Assert.AreEqual("archive-local-publication-index-pending", pending.Reason);
        Assert.AreEqual(disabled.VerifiedCutoff, pending.VerifiedCutoff);
        Assert.AreEqual(disabled.LatestVerifiedReceipt, pending.LatestVerifiedReceipt);
        var persisted = await restarted.FindAsync(work.Publication.GroupId, work.Publication.RevisionId, token).ConfigureAwait(false);
        Assert.AreEqual(disabled.VerifiedCutoff, persisted?.Receipt);
    }

    [TestMethod]
    public async Task UnavailableCheckpointBlocksPrefixAndRetriesAfterBoundedBackoff()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var clock = silo.Clock;
        var (settings, store) = LocalStore();
        InstallBinding(settings, store);
        using var installed = OpenLocal(settings, store, Path.Combine(silo.RootPath, "installed"), clock);
        await installed.VerifyReadinessAsync(token).ConfigureAwait(false);
        var genesis = await installed.State.ReadCommittedSegmentAsync(new(1, 1), token).ConfigureAwait(false);
        Assert.IsNotNull(genesis);

        var key = SiloObjectNamespace.CheckpointKey(genesis.Position);
        var version = store.Plant(key, SiloArchiveFixture.Payload);
        var anchor = new SiloAnchorRecord(store.Locator(key, version), SiloArchiveFixture.Payload.LongLength,
            SiloHash.Sha256(SiloArchiveFixture.Payload), genesis.Position, genesis.Sha256);
        var anchored = await installed.ArchiveJournal.AppendAsync([anchor], "checkpoint-fixture", token).ConfigureAwait(false);
        var fixture = await SiloArchiveFixture.StartAsync(installed.Binding.BindingId, token).ConfigureAwait(false);
        await using var lease = fixture.ConfigureAwait(false);
        fixture.Replicate(store, genesis);
        fixture.Replicate(store, anchored);
        fixture.Replicate(store, anchor.Checkpoint);
        fixture.UnavailableKey = key;

        using var services = new ServiceCollection().AddSingleton(installed).BuildServiceProvider();
        using var worker = new SiloArchiveWorker(fixture.Configuration, services, clock, NullLogger<SiloArchiveWorker>.Instance);
        var blocked = await worker.RunCycleAsync(token).ConfigureAwait(false);
        Assert.AreEqual("blocked", blocked.State);
        Assert.AreEqual("archive-checkpoint-not-verified", blocked.Reason);
        Assert.AreEqual(1, fixture.Gets(key));

        Assert.AreEqual(blocked, await worker.RunCycleAsync(token).ConfigureAwait(false));
        Assert.AreEqual(1, fixture.Gets(key));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual("blocked", (await worker.RunCycleAsync(token).ConfigureAwait(false)).State);
        Assert.AreEqual(2, fixture.Gets(key));
        fixture.UnavailableKey = null;
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual("blocked", (await worker.RunCycleAsync(token).ConfigureAwait(false)).State);
        Assert.AreEqual(2, fixture.Gets(key), "The second attempt must back off longer.");
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.AreEqual("reachable", (await worker.RunCycleAsync(token).ConfigureAwait(false)).State);
        Assert.AreEqual(3, fixture.Gets(key));
        var index = new SiloArchiveIndex(installed.PhysicalState.Resolve(fixture.Marker.StoreId + ".db"), fixture.Marker);
        Assert.AreEqual((anchored.Position.Sequence, anchored.Sha256), await index.ReadPrefixAsync(token).ConfigureAwait(false));
    }

    private static async Task<(string Main, string Wal)> FingerprintAsync(string path, CancellationToken token) =>
        (SiloHash.Sha256(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false)),
            SiloHash.Sha256(await File.ReadAllBytesAsync(path + "-wal", token).ConfigureAwait(false)));

    private static SiloArchiveMarker Marker(SiloStorageBinding binding) => new(SiloArchiveStore.MarkerFormat,
        SiloArchiveFixture.StoreId, SiloArchiveFixture.Bucket, binding.BindingId, SiloArchiveStore.RetentionPolicy);

    private static (SiloInstalledSettings Settings, SiloTestStore Store) LocalStore()
    {
        var instance = Guid.NewGuid();
        var binding = new SiloStorageBinding(instance.ToString("D"), CameraAgentSiloBinding.StoreIdFor(instance),
            CameraAgentSiloBinding.BucketFor(instance));
        var configuration = new ConfigurationManager();
        var section = configuration.GetSection("Silo");
        section["StateContract"] = CameraAgentSiloBinding.StateContract;
        section["StoreId"] = binding.StoreId;
        section["Bucket"] = binding.Bucket;
        section["StorageBindingId"] = binding.BindingId;
        section["WriterGeneration"] = "1";
        section["Endpoint"] = "http://127.0.0.1";
        section["AccessKey"] = "testwriter00000001";
        section["SecretKey"] = "testwritersecret00000001";
        return (SiloInstalledSettings.Read(section), new SiloTestStore(binding.StoreId, binding.Bucket));
    }

    private static void InstallBinding(SiloInstalledSettings settings, SiloTestStore store)
    {
        var binding = settings.Binding;
        store.Plant(CameraAgentSiloBinding.MarkerKey, JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = CameraAgentSiloBinding.MarkerFormat,
            instanceId = binding.InstanceId,
            storeId = binding.StoreId,
            bucket = binding.Bucket,
            storageBindingId = binding.BindingId,
            stateContract = CameraAgentSiloBinding.StateContract
        }));
        store.Plant(CameraAgentSiloBinding.FenceKey, JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = CameraAgentSiloBinding.FenceFormat,
            instanceId = binding.InstanceId,
            storageBindingId = binding.BindingId,
            writerGeneration = 1,
            writerAccessKeyId = settings.AccessKey
        }));
    }

    private static SiloInstalledStorage OpenLocal(SiloInstalledSettings settings, SiloTestStore store, string root, TimeProvider clock) =>
        new(settings, Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }), clock, faults: null, decorateReadStore: null,
            new SiloInstalledStore(settings, store, _ => ValueTask.FromResult(true)), new SiloTestPayloads());
}
