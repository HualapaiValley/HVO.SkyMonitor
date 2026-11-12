using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Tests.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Environmental;

public sealed partial class EnvironmentalCaptureFailureTests
{
    [TestMethod]
    public async Task ExpiredCaptureReceiptRemainsUnavailableWhenOldFactsAreStillPinned()
    {
        var fixture = await AttributionFixture.CreateAsync(0).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        var pin = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, null, Epoch.AddSeconds(-50)).ConfigureAwait(false);
        var service = new EnvironmentalAssociationService(fixture.Store, fixture.Store, fixture.HostOptions, fixture.Clock,
            acquisitionStates: fixture.Store);
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, (await service.AssociateAsync(
            pin.Manifest.Descriptor.Capture.CaptureId, pin.Manifest.Descriptor.Capture.CaptureSequence,
            Epoch.AddSeconds(-50), Epoch.AddSeconds(-30), null, [HVO.SkyMonitor.Processing.EnvironmentalObservationKind.SkyQuality],
            CancellationToken.None).ConfigureAwait(false)).Single().Status);
        fixture.Clock.Now = Epoch;
        var acquisition = await fixture.Bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(acquisition);
        await fixture.Device.Started(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        fixture.Device.Release(2, fail: true);
        await acquisition.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var raw = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, acquisition.Id, Epoch).ConfigureAwait(false);
        await fixture.Store.BindCaptureAcquisitionAsync(fixture.Root, acquisition.Id, raw.Manifest.Descriptor.Capture.CaptureId,
            CancellationToken.None).ConfigureAwait(false);
        fixture.Clock.Now = Epoch.AddSeconds(10);
        var poll = fixture.Coordinator.AcquireSourceAsync("sqm", EnvironmentalAcquisitionTrigger.Periodic, fixture.Clock.Now).AsTask();
        await fixture.Device.Started(3).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        fixture.Device.Release(3, fail: false);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Produced, (await poll.ConfigureAwait(false)).Disposition);
        fixture.Clock.Now = Epoch.AddDays(32);
        await fixture.Store.RetainLocalAsync(fixture.Root, fixture.Clock.Now.AddDays(-31), 100, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, (await CaptureMetadataCountsAsync(fixture.Root).ConfigureAwait(false)).Receipts);
        Assert.IsGreaterThan(0L, (await fixture.Store.GetLocalSnapshotAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false)).StoredCount);
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Missing, (await fixture.AssociateAsync(
            raw.Manifest.Descriptor.Capture.CaptureId, raw.Manifest.Descriptor.Capture.CaptureSequence, Epoch, acquisition.Id)
            .ConfigureAwait(false)).Status);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public async Task CaptureCompletionFailuresRetryAndHaveABoundedUnavailableOutcome(int failures)
    {
        var fixture = await AttributionFixture.CreateAsync(0, completionFailures: failures).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        fixture.Clock.Now = Epoch;
        var acquisition = await fixture.Bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(acquisition);
        await fixture.Device.Started(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        fixture.Clock.Now = Epoch.AddMilliseconds(500);
        fixture.Device.Release(2, fail: false);
        await acquisition.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.IsGreaterThanOrEqualTo(2, fixture.CompletionAttempts);
        var capture = Guid.NewGuid();
        if (failures == 3)
        {
            Assert.IsTrue((await fixture.Store.ReadCaptureAvailabilityAsync(fixture.Root, capture, acquisition.Id,
                CancellationToken.None).ConfigureAwait(false)).Pending);
            fixture.Clock.Now = Epoch.AddMinutes(13);
            var expired = await fixture.Store.ReadCaptureAvailabilityAsync(fixture.Root, capture, acquisition.Id,
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(expired.Pending);
            CollectionAssert.Contains(expired.UnavailableSources.ToArray(), "sqm");
        }
        Assert.AreEqual(failures == 3 ? LocalEnvironmentalAssociationStatus.Missing : LocalEnvironmentalAssociationStatus.Fresh,
            (await fixture.AssociateAsync(capture, 1, Epoch, acquisition.Id).ConfigureAwait(false)).Status);
    }

    [TestMethod]
    public async Task ProducedCaptureSurvivesShutdownWhileAnUnrelatedCoalescedPollIsBlocked()
    {
        var fixture = await AttributionFixture.CreateAsync(0).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        fixture.Clock.Now = Epoch;
        var acquisition = await fixture.Bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(acquisition);
        await fixture.Device.Started(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.AreEqual(EnvironmentalAcquisitionDisposition.Coalesced, (await fixture.Coordinator.AcquireSourceAsync(
            "sqm", EnvironmentalAcquisitionTrigger.Periodic, Epoch).ConfigureAwait(false)).Disposition);
        fixture.Clock.Now = Epoch.AddMilliseconds(500);
        fixture.Device.Release(2, fail: false);
        await fixture.Device.Started(3).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var raw = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, acquisition.Id, Epoch).ConfigureAwait(false);
        await fixture.StopAsync().ConfigureAwait(false);
        fixture.Reopen();
        await fixture.Store.RecoverCaptureAcquisitionsAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false);
        var association = await fixture.AssociateAsync(raw.Manifest.Descriptor.Capture.CaptureId,
            raw.Manifest.Descriptor.Capture.CaptureSequence, Epoch, acquisition.Id).ConfigureAwait(false);
        Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, association.Status,
            "Shutdown of the later periodic read must not erase this capture's produced receipt.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PendingCaptureWaitDoesNotQuarantineTheRealLaneOrRefuseTheNextCapture(bool expires)
    {
        var fixture = await AttributionFixture.CreateAsync(0).ConfigureAwait(false);
        await using var cleanup = fixture.ConfigureAwait(false);
        fixture.Clock.Now = Epoch;
        var acquisition = await fixture.Bridge.BeforeCaptureAsync(Epoch, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(acquisition);
        await fixture.Device.Started(2).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var raw = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, acquisition.Id, Epoch,
            fixture.HostOptions.Value.EnvironmentalAcquisition).ConfigureAwait(false);
        var policy = new CaptureLanePolicy(fixture.HostOptions);
        var lane = policy.Definitions.Single(item => item.Name == "environment-association");
        var lanes = new SqliteCaptureLaneStore(fixture.Root, 1, fixture.HostOptions.Value.CaptureDistribution,
            policy, fixture.Clock, new NullCaptureLaneFaultInjector());
        await lanes.InitializeLanesAsync(CancellationToken.None).ConfigureAwait(false);
        using var services = new ServiceCollection().AddSingleton(fixture.Coordinator).AddSingleton(fixture.HostOptions)
            .AddSingleton(new EnvironmentalAssociationService(fixture.Store, fixture.Store, fixture.HostOptions, fixture.Clock,
                acquisitionStates: fixture.Store)).BuildServiceProvider();
        var handler = new EnvironmentalAssociationCaptureLaneHandler(services);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            fixture.Clock.Now = Epoch.AddSeconds(21 + attempt * 10);
            var lease = await lanes.ClaimAsync(lane, "pending-capture-test", RawCaptureIngressTests.EnvironmentalConfiguration(),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            var result = await handler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
            _ = await lanes.FailAsync(lease, result, CancellationToken.None).ConfigureAwait(false);
        }
        var backlog = (await lanes.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false)).Single(item => item.Lane == lane.Name);
        Assert.AreEqual(0L, backlog.QuarantineCount, "Pending acquisition must not consume the lane's failure attempts.");
        await lanes.EnsureCanAcceptAsync(4, CancellationToken.None).ConfigureAwait(false);
        var next = await fixture.Bridge.BeforeCaptureAsync(fixture.Clock.Now, CancellationToken.None).ConfigureAwait(false);
        var nextRaw = await RawCaptureIngressTests.PersistEnvironmentalCaptureAsync(fixture.Root, next!.Id, fixture.Clock.Now,
            fixture.HostOptions.Value.EnvironmentalAcquisition).ConfigureAwait(false);
        Assert.AreNotEqual(raw.Manifest.Descriptor.Capture.CaptureId, nextRaw.Manifest.Descriptor.Capture.CaptureId);
        if (!expires)
        {
            fixture.Device.Release(2, fail: false);
            await acquisition.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        fixture.Clock.Now = expires ? Epoch.AddMinutes(13) : Epoch.AddSeconds(90);
        var ready = await lanes.ClaimAsync(lane, "pending-capture-test", RawCaptureIngressTests.EnvironmentalConfiguration(),
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(ready);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed,
            (await handler.HandleAsync(ready.Context, CancellationToken.None).ConfigureAwait(false)).Outcome);
        await lanes.CompleteAsync(ready, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(expires ? LocalEnvironmentalAssociationStatus.Missing : LocalEnvironmentalAssociationStatus.Fresh,
            (await fixture.Store.ReadAssociationsAsync(fixture.Root, raw.Manifest.Descriptor.Capture.CaptureId,
                CancellationToken.None).ConfigureAwait(false)).Single().Status);
        await lanes.EnsureCanAcceptAsync(4, CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CaptureReceiptAndAttemptHistoryExpiresWhileRawRowsRemain(bool rawJournalPresent)
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-expiry-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock { Now = Epoch };
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox(clock);
            await store.InitializeAsync(root, CancellationToken.None).ConfigureAwait(false);
            if (rawJournalPresent) await CreateRetentionRawJournalAsync(root).ConfigureAwait(false);
            var source = EnvironmentalHardwareAdapterTests.Source("SkyQualityMeter").Descriptor;
            for (var day = 0; day < 40; day++)
            {
                clock.Now = Epoch.AddDays(day);
                var capture = Guid.NewGuid();
                var scope = Guid.NewGuid();
                var key = EnvironmentalCaptureAcquisition.Prefix(scope) + "source";
                var claim = await store.ClaimOnDemandAsync(root, key, new string('A', 64), source.Id, "capture-before", null,
                    clock.Now, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
                await store.CompleteOnDemandAsync(root, key, claim.LeaseToken,
                    new(source.Id, EnvironmentalAcquisitionTrigger.BeforeCapture, EnvironmentalAcquisitionDisposition.Failed,
                        "scripted", null, clock.Now, clock.Now), CancellationToken.None).ConfigureAwait(false);
                await store.BindCaptureAcquisitionAsync(root, scope, capture, CancellationToken.None).ConfigureAwait(false);
                if (rawJournalPresent) await AddRetentionRawCaptureAsync(root, capture, Encoding.UTF8.GetBytes("{}")).ConfigureAwait(false);
                foreach (var trigger in new[] { EnvironmentalAcquisitionTrigger.AfterCapture, EnvironmentalAcquisitionTrigger.EveryNthCapture,
                    EnvironmentalAcquisitionTrigger.RegimeChange })
                    await store.RecordAttemptAsync(root, source, new(source.Id, trigger, EnvironmentalAcquisitionDisposition.Failed,
                        "scripted", null, clock.Now, clock.Now), day + 1, capture, CancellationToken.None).ConfigureAwait(false);
                await store.RetainLocalAsync(root, clock.Now.AddDays(-31), 1000, CancellationToken.None).ConfigureAwait(false);
            }
            var growth = await CaptureMetadataCountsAsync(root).ConfigureAwait(false);
            clock.Now = Epoch.AddDays(72);
            await store.RetainLocalAsync(root, clock.Now.AddDays(-31), 1000, CancellationToken.None).ConfigureAwait(false);
            var expired = await CaptureMetadataCountsAsync(root).ConfigureAwait(false);
            Assert.AreEqual((32L, 96L, 0L, 0L), (growth.Receipts, growth.Attempts, expired.Receipts, expired.Attempts));
            if (rawJournalPresent)
            {
                using var raw = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
                await raw.OpenAsync().ConfigureAwait(false);
                using var count = raw.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM raw_captures;";
                Assert.AreEqual(40L, (long)(await count.ExecuteScalarAsync().ConfigureAwait(false))!);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task OrphanReceiptExpiryDoesNotParseUnrelatedRawManifests()
    {
        var root = Path.Combine(Path.GetTempPath(), "orphan-expiry-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock { Now = Epoch };
        try
        {
            using var store = new SqliteEnvironmentalObservationOutbox(clock);
            await store.InitializeAsync(root, CancellationToken.None).ConfigureAwait(false);
            await CreateRetentionRawJournalAsync(root).ConfigureAwait(false);
            await AddRetentionRawCaptureAsync(root, Guid.NewGuid(), Encoding.UTF8.GetBytes("unrelated-unparseable-manifest")).ConfigureAwait(false);
            var key = EnvironmentalCaptureAcquisition.Prefix(Guid.NewGuid()) + "source";
            var claim = await store.ClaimOnDemandAsync(root, key, new string('A', 64), "sqm", "capture-before", null,
                Epoch, TimeSpan.FromMinutes(1), CancellationToken.None).ConfigureAwait(false);
            await store.CompleteOnDemandAsync(root, key, claim.LeaseToken,
                new("sqm", EnvironmentalAcquisitionTrigger.BeforeCapture, EnvironmentalAcquisitionDisposition.Missing,
                    "scripted", null, Epoch, Epoch), CancellationToken.None).ConfigureAwait(false);
            clock.Now = Epoch.AddDays(32);
            await store.RetainLocalAsync(root, clock.Now.AddDays(-31), 100, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0L, (await CaptureMetadataCountsAsync(root).ConfigureAwait(false)).Receipts);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task CreateRetentionRawJournalAsync(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "journal"));
        using var raw = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await raw.OpenAsync().ConfigureAwait(false);
        using var create = raw.CreateCommand(); create.CommandText = "CREATE TABLE raw_captures(capture_id TEXT PRIMARY KEY, manifest_json BLOB);";
        await create.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task AddRetentionRawCaptureAsync(string root, Guid id, byte[] manifest)
    {
        using var raw = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await raw.OpenAsync().ConfigureAwait(false);
        using var insert = raw.CreateCommand(); insert.CommandText = "INSERT INTO raw_captures VALUES($capture,$manifest);";
        insert.Parameters.AddWithValue("$capture", id.ToString("N")); insert.Parameters.AddWithValue("$manifest", manifest);
        await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<(long Receipts, long Attempts)> CaptureMetadataCountsAsync(string root)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(root, ".environment", "environmental-observation-outbox.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var count = connection.CreateCommand(); count.CommandText = "SELECT COUNT(*) FROM environmental_on_demand_commands;";
        var receipts = (long)(await count.ExecuteScalarAsync().ConfigureAwait(false))!;
        count.CommandText = "SELECT COUNT(*) FROM environmental_acquisition_attempts;";
        return (receipts, (long)(await count.ExecuteScalarAsync().ConfigureAwait(false))!);
    }
}
