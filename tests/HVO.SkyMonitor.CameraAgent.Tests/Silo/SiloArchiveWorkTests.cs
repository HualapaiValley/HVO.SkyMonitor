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
        await index.CompleteAttemptAsync(work[0], "reachable", null, now, first, now, resetAttempts: true, token).ConfigureAwait(false);
        await index.CompleteAttemptAsync(work[2], "reachable", null, now, third, now, resetAttempts: true, token).ConfigureAwait(false);
        await index.SavePrefixAsync(7, third.Sha256, token).ConfigureAwait(false);
        Assert.AreEqual(work[0].Publication.GroupId, (await index.ReadBacklogAsync(token).ConfigureAwait(false)).Cutoff?.GroupId);
        await index.CompleteAttemptAsync(work[0], "policy-expired", null, now, null, now, resetAttempts: false, token, expirySequence: 7)
            .ConfigureAwait(false);
        Assert.IsNull((await index.ReadBacklogAsync(token).ConfigureAwait(false)).Cutoff);
        await index.CompleteAttemptAsync(work[1], "reachable", null, now, second, now, resetAttempts: true, token).ConfigureAwait(false);

        var caughtUp = await index.ReadBacklogAsync(token).ConfigureAwait(false);
        Assert.AreEqual(0L, caughtUp.Count);
        Assert.AreEqual(work[2].Publication.GroupId, caughtUp.Cutoff?.GroupId);
        var expired = await index.FindAsync(work[0].Publication.GroupId, work[0].Publication.RevisionId, token).ConfigureAwait(false);
        Assert.AreEqual("policy-expired", expired?.State);
        Assert.AreEqual(first.VersionId, expired?.Receipt?.ReceiptVersionId);
        Assert.IsFalse((await index.ReadDueAsync(now, 10, token).ConfigureAwait(false)).Any(row => row.Publication == work[0].Publication));
    }

    [TestMethod]
    public async Task ExpiredArchiveCopyCannotSettleAnotherRequiredMemberOrMoveTheCutoff()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var (settings, store) = LocalStore();
        InstallBinding(settings, store);
        using var source = OpenLocal(settings, store, Path.Combine(silo.RootPath, "installed"), silo.Clock, silo.Payloads);
        await source.VerifyReadinessAsync(token).ConfigureAwait(false);
        var fixture = await SiloArchiveFixture.StartAsync(source.Binding.BindingId, token).ConfigureAwait(false);
        await using var lease = fixture.ConfigureAwait(false);
        var first = await PublishInstalledAsync(source, store, fixture, SiloDrafts.Capture(silo.Payloads, 1), token, replicateMembers: false)
            .ConfigureAwait(false);
        var second = await PublishInstalledAsync(source, store, fixture, SiloDrafts.Capture(silo.Payloads, 2), token).ConfigureAwait(false);
        fixture.WriteExpiry(first.Members[0].Locator, silo.Clock.GetUtcNow());
        fixture.Replicate(store, first.Members[1].Locator);
        fixture.UnavailableKey = first.Members[1].Locator.Key;

        // A pre-fix projection may have already declared this publication terminal.
        var index = new SiloArchiveIndex(source.PhysicalState.Resolve(fixture.Marker.StoreId + ".db"), fixture.Marker);
        await index.InitializeAsync(token).ConfigureAwait(false);
        await index.DiscoverAsync(source.Index, 1, token).ConfigureAwait(false);
        using (var connection = await SiloSqlite.OpenAsync(source.PhysicalState.Resolve(fixture.Marker.StoreId + ".db"), token).ConfigureAwait(false))
            await SiloSqlite.ExecuteAsync(connection, null, """
                UPDATE archive_work SET state = 'policy-expired', reason = 'archive-publication-policy-expired';
                """, token).ConfigureAwait(false);
        using var services = new ServiceCollection().AddSingleton(source).BuildServiceProvider();
        using var worker = new SiloArchiveWorker(fixture.Configuration, services, silo.Clock, NullLogger<SiloArchiveWorker>.Instance);
        await worker.RunCycleAsync(token).ConfigureAwait(false);
        await ReplicateJournalAsync(source, store, fixture, token).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromSeconds(2));
        var status = await worker.RunCycleAsync(token).ConfigureAwait(false);

        Assert.AreEqual("blocked", status.State);
        Assert.AreEqual(1L, status.BacklogCount);
        Assert.AreEqual(second.GroupId, status.LatestVerifiedReceipt?.GroupId);
        Assert.IsNull(status.VerifiedCutoff);
        var unresolved = await worker.FindAsync(first.GroupId, first.RevisionId, token).ConfigureAwait(false);
        Assert.AreEqual("archive-version-unreachable", unresolved?.Reason);
        Assert.IsNull(unresolved?.Receipt);

        fixture.UnavailableKey = null;
        silo.Clock.Advance(TimeSpan.FromSeconds(4));
        var settled = await worker.RunCycleAsync(token).ConfigureAwait(false);
        Assert.AreEqual("pending", (await worker.FindAsync(first.GroupId, first.RevisionId, token).ConfigureAwait(false))?.State);
        Assert.AreEqual(1L, settled.BacklogCount);
        Assert.IsNull(settled.VerifiedCutoff);
        await ReplicateJournalAsync(source, store, fixture, token).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromSeconds(8));
        settled = await worker.RunCycleAsync(token).ConfigureAwait(false);
        Assert.AreEqual("reachable", (await worker.FindAsync(first.GroupId, first.RevisionId, token).ConfigureAwait(false))?.State);
        Assert.AreEqual(0L, settled.BacklogCount);
        Assert.AreEqual(second.GroupId, settled.VerifiedCutoff?.GroupId);

    }

    [TestMethod]
    [DataRow(false, "preview")]
    [DataRow(true, "preview")]
    [DataRow(false, "manifest")]
    [DataRow(false, "expiredManifest")]
    public async Task MixedExpiryPersistsSurvivingProofAndRechecksAfterRestart(bool firstReceiptEarned, string damaged)
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var (settings, store) = LocalStore();
        InstallBinding(settings, store);
        using var source = OpenLocal(settings, store, Path.Combine(silo.RootPath, "installed"), silo.Clock, silo.Payloads);
        await source.VerifyReadinessAsync(token).ConfigureAwait(false);
        var fixture = await SiloArchiveFixture.StartAsync(source.Binding.BindingId, token).ConfigureAwait(false);
        await using var lease = fixture.ConfigureAwait(false);
        fixture.Settings["ReadScratchRoot"] = silo.RootPath;
        var first = await PublishInstalledAsync(source, store, fixture, SiloDrafts.Capture(silo.Payloads, 1), token).ConfigureAwait(false);
        var second = await PublishInstalledAsync(source, store, fixture, SiloDrafts.Capture(silo.Payloads, 2), token).ConfigureAwait(false);
        var raw = first.Members[0];
        var preview = first.Members[1];
        using var services = new ServiceCollection().AddSingleton(source).BuildServiceProvider();
        using var worker = new SiloArchiveWorker(fixture.Configuration, services, silo.Clock, NullLogger<SiloArchiveWorker>.Instance);
        CameraAgentSiloArchiveReceipt? original = null;
        if (firstReceiptEarned)
        {
            await worker.RunCycleAsync(token).ConfigureAwait(false);
            await ReplicateJournalAsync(source, store, fixture, token).ConfigureAwait(false);
            silo.Clock.Advance(TimeSpan.FromSeconds(2));
            await worker.RunCycleAsync(token).ConfigureAwait(false);
            original = (await worker.FindAsync(first.GroupId, first.RevisionId, token).ConfigureAwait(false))?.Receipt;
            Assert.IsNotNull(original);
            silo.Clock.Advance(TimeSpan.FromDays(1));
        }

        fixture.WriteExpiry(raw.Locator, silo.Clock.GetUtcNow());
        fixture.Delete(raw.Locator);
        if (damaged == "expiredManifest")
        {
            await JournalLocalExpiryAsync(source, store, SiloHarness.ManifestIdentity(first), first.Manifest.Locator, silo.Clock.GetUtcNow(), token)
                .ConfigureAwait(false);
            fixture.Delete(first.Manifest.Locator);
            await ReplicateJournalAsync(source, store, fixture, token).ConfigureAwait(false);
        }
        await worker.RunCycleAsync(token).ConfigureAwait(false);
        await ReplicateJournalAsync(source, store, fixture, token).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromSeconds(2));
        var settled = await worker.RunCycleAsync(token).ConfigureAwait(false);
        if (firstReceiptEarned || damaged == "expiredManifest")
        {
            await ReplicateJournalAsync(source, store, fixture, token).ConfigureAwait(false);
            silo.Clock.Advance(TimeSpan.FromSeconds(4));
            settled = await worker.RunCycleAsync(token).ConfigureAwait(false);
        }

        Assert.AreEqual("reachable", settled.State);
        Assert.AreEqual(second.GroupId, settled.VerifiedCutoff?.GroupId);
        var receipt = (await worker.FindAsync(first.GroupId, first.RevisionId, token).ConfigureAwait(false))?.Receipt;
        Assert.IsNotNull(receipt);
        Assert.AreNotEqual(original?.ReceiptSha256, receipt.ReceiptSha256);
        var segment = SiloJson.ReadSegment(store.Read(SiloObjectNamespace.SegmentKey(new(receipt.ReceiptGeneration, receipt.ReceiptSequence)),
            receipt.ReceiptVersionId), "partial receipt");
        Assert.AreEqual(new SiloArchiveMemberAvailability(raw.ArtifactId, raw.ArtifactRevision, "policy-expired"),
            ((SiloArchiveReceiptRecord)segment.Records[0]).UnverifiedMembers?.Single());
        var expectedCopies = damaged == "expiredManifest" ? new[] { preview.Sha256 } : [first.Manifest.Sha256, preview.Sha256];
        CollectionAssert.AreEquivalent(expectedCopies,
            segment.Records.OfType<SiloCopyRecord>().Select(copy => copy.Identity.Sha256).ToArray());
        using (var read = await worker.ReadAsync(new(receipt, preview.ArtifactId, preview.ArtifactRevision), token).ConfigureAwait(false))
            Assert.AreEqual(preview.Sha256, read.Sha256);

        worker.Dispose();
        using var restarted = new SiloArchiveWorker(fixture.Configuration, services, silo.Clock, NullLogger<SiloArchiveWorker>.Instance);
        if (damaged == "expiredManifest")
        {
            fixture.WriteExpiry(preview.Locator, silo.Clock.GetUtcNow());
            fixture.Delete(preview.Locator);
            silo.Clock.Advance(TimeSpan.FromDays(1));
            var expired = await restarted.RunCycleAsync(token).ConfigureAwait(false);
            Assert.AreEqual("policy-expired", (await restarted.FindAsync(first.GroupId, first.RevisionId, token).ConfigureAwait(false))?.State);
            Assert.AreEqual(0L, expired.BacklogCount);
            Assert.AreEqual(second.GroupId, expired.VerifiedCutoff?.GroupId);
            return;
        }
        var lost = damaged == "manifest" ? first.Manifest.Locator : preview.Locator;
        if (firstReceiptEarned)
        {
            var damagedBytes = store.Read(lost.Key, lost.VersionId);
            damagedBytes[0] ^= 0x80;
            fixture.Put(lost.Key, lost.VersionId, damagedBytes);
        }
        else
        {
            fixture.Delete(lost);
        }
        silo.Clock.Advance(TimeSpan.FromDays(1));
        var failure = await restarted.RunCycleAsync(token).ConfigureAwait(false);
        Assert.AreEqual(firstReceiptEarned ? "failed" : "pending", failure.State);
        Assert.AreEqual(1L, failure.BacklogCount);
        Assert.IsNull(failure.VerifiedCutoff);
    }

    [TestMethod]
    [DataRow(true, "failed")]
    [DataRow(false, "missing")]
    public async Task RecheckDropsAnUnverifiedOptionalArchiveCopy(bool corrupt, string expectedAvailability)
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var (settings, store) = LocalStore();
        InstallBinding(settings, store);
        using var source = OpenLocal(settings, store, Path.Combine(silo.RootPath, "installed"), silo.Clock, silo.Payloads);
        await source.VerifyReadinessAsync(token).ConfigureAwait(false);
        var fixture = await SiloArchiveFixture.StartAsync(source.Binding.BindingId, token).ConfigureAwait(false);
        await using var lease = fixture.ConfigureAwait(false);
        fixture.Settings["ReadScratchRoot"] = silo.RootPath;
        var publication = await PublishInstalledAsync(source, store, fixture, SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false), token)
            .ConfigureAwait(false);
        var raw = publication.Members.Single(member => !member.Required);
        var preview = publication.Members.Single(member => member.Required);
        using var services = new ServiceCollection().AddSingleton(source).BuildServiceProvider();
        using var worker = new SiloArchiveWorker(fixture.Configuration, services, silo.Clock, NullLogger<SiloArchiveWorker>.Instance);
        await worker.RunCycleAsync(token).ConfigureAwait(false);
        await ReplicateJournalAsync(source, store, fixture, token).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromSeconds(2));
        var initial = await worker.RunCycleAsync(token).ConfigureAwait(false);
        var original = initial.VerifiedCutoff;
        Assert.IsNotNull(original);
        Assert.IsTrue(ReadReceipt(original).Records.OfType<SiloCopyRecord>().Any(copy => copy.Identity.Id == raw.ArtifactId));

        if (corrupt)
        {
            var bytes = store.Read(raw.Locator.Key, raw.Locator.VersionId);
            bytes[0] ^= 0x80;
            fixture.Put(raw.Locator.Key, raw.Locator.VersionId, bytes);
        }
        else
        {
            fixture.Delete(raw.Locator);
        }

        var gets = fixture.Gets(raw.Locator.Key);
        silo.Clock.Advance(TimeSpan.FromDays(1));
        var rechecking = await worker.RunCycleAsync(token).ConfigureAwait(false);
        Assert.IsGreaterThan(gets, fixture.Gets(raw.Locator.Key));
        Assert.IsNull(rechecking.VerifiedCutoff);
        Assert.IsNull(rechecking.LatestVerifiedReceipt);
        Assert.AreEqual("pending", (await worker.FindAsync(publication.GroupId, publication.RevisionId, token).ConfigureAwait(false))?.State);

        await ReplicateJournalAsync(source, store, fixture, token).ConfigureAwait(false);
        using var restarted = new SiloArchiveWorker(fixture.Configuration, services, silo.Clock, NullLogger<SiloArchiveWorker>.Instance);
        silo.Clock.Advance(TimeSpan.FromSeconds(2));
        await restarted.RunCycleAsync(token).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromSeconds(4));
        var settled = await restarted.RunCycleAsync(token).ConfigureAwait(false);
        var lookup = await restarted.FindAsync(publication.GroupId, publication.RevisionId, token).ConfigureAwait(false);
        var refreshed = lookup?.Receipt;
        Assert.IsNotNull(refreshed);
        Assert.AreEqual("reachable", lookup?.State);
        Assert.AreEqual("reachable", settled.State);
        Assert.AreEqual(0L, settled.BacklogCount);
        Assert.AreEqual(refreshed, settled.VerifiedCutoff);
        Assert.AreNotEqual(original.ReceiptSha256, refreshed.ReceiptSha256);
        var segment = ReadReceipt(refreshed);
        CollectionAssert.AreEquivalent(new[] { publication.Manifest.Sha256, preview.Sha256 },
            segment.Records.OfType<SiloCopyRecord>().Select(copy => copy.Identity.Sha256).ToArray());
        var unverified = ((SiloArchiveReceiptRecord)segment.Records[0]).UnverifiedMembers;
        if (corrupt)
            Assert.AreEqual(new SiloArchiveMemberAvailability(raw.ArtifactId, raw.ArtifactRevision, "failed"), unverified?.Single());
        else
            Assert.IsNull(unverified);
        using (var optional = await restarted.ReadAsync(new(refreshed, raw.ArtifactId, raw.ArtifactRevision), token).ConfigureAwait(false))
            Assert.AreEqual(expectedAvailability, optional.State);
        using (var required = await restarted.ReadAsync(new(refreshed, preview.ArtifactId, preview.ArtifactRevision), token).ConfigureAwait(false))
            Assert.AreEqual(preview.Sha256, required.Sha256);

        var head = await source.State.ReadHeadAsync(token).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromDays(1));
        var unchanged = await restarted.RunCycleAsync(token).ConfigureAwait(false);
        Assert.AreEqual(refreshed.ReceiptSha256, unchanged.VerifiedCutoff?.ReceiptSha256);
        Assert.AreEqual(head, await source.State.ReadHeadAsync(token).ConfigureAwait(false));

        SiloJournalSegment ReadReceipt(CameraAgentSiloArchiveReceipt receipt) => SiloJson.ReadSegment(
            store.Read(SiloObjectNamespace.SegmentKey(new(receipt.ReceiptGeneration, receipt.ReceiptSequence)), receipt.ReceiptVersionId), "archive receipt");
    }

    [TestMethod]
    public async Task JournaledLocalLastCopyExpiryWaitsForTheAuthorizingSegment()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var (settings, store) = LocalStore();
        InstallBinding(settings, store);
        using var source = OpenLocal(settings, store, Path.Combine(silo.RootPath, "installed"), silo.Clock, silo.Payloads);
        await source.VerifyReadinessAsync(token).ConfigureAwait(false);
        var fixture = await SiloArchiveFixture.StartAsync(source.Binding.BindingId, token).ConfigureAwait(false);
        await using var lease = fixture.ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false);
        var first = await PublishInstalledAsync(source, store, fixture, draft, token, replicateMembers: false).ConfigureAwait(false);
        var second = await PublishInstalledAsync(source, store, fixture, SiloDrafts.Capture(silo.Payloads, 2), token).ConfigureAwait(false);
        using var services = new ServiceCollection().AddSingleton(source).BuildServiceProvider();
        using var worker = new SiloArchiveWorker(fixture.Configuration, services, silo.Clock, NullLogger<SiloArchiveWorker>.Instance);
        await worker.RunCycleAsync(token).ConfigureAwait(false);
        await ReplicateJournalAsync(source, store, fixture, token).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromSeconds(2));
        var initial = await worker.RunCycleAsync(token).ConfigureAwait(false);
        Assert.AreEqual(second.GroupId, initial.LatestVerifiedReceipt?.GroupId);

        var member = first.Members.Single(member => member.Required);
        var identity = SiloHarness.Identity(first, member.ArtifactId);
        await JournalLocalExpiryAsync(source, store, identity, member.Locator, silo.Clock.GetUtcNow(), token).ConfigureAwait(false);

        silo.Clock.Advance(TimeSpan.FromSeconds(4));
        var awaitingExpiry = await worker.RunCycleAsync(token).ConfigureAwait(false);
        Assert.AreEqual(1L, awaitingExpiry.BacklogCount);
        Assert.AreEqual(second.GroupId, awaitingExpiry.LatestVerifiedReceipt?.GroupId);
        Assert.IsNull(awaitingExpiry.VerifiedCutoff);
        Assert.IsNull((await worker.FindAsync(first.GroupId, first.RevisionId, token).ConfigureAwait(false))?.Receipt);

        await ReplicateJournalAsync(source, store, fixture, token).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromSeconds(2));
        await worker.RunCycleAsync(token).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromSeconds(6));
        await worker.RunCycleAsync(token).ConfigureAwait(false);
        await ReplicateJournalAsync(source, store, fixture, token).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromSeconds(16));
        var status = await worker.RunCycleAsync(token).ConfigureAwait(false);

        var settled = await worker.FindAsync(first.GroupId, first.RevisionId, token).ConfigureAwait(false);
        Assert.AreEqual("reachable", settled?.State);
        Assert.IsNotNull(settled?.Receipt);
        Assert.AreEqual(0L, status.BacklogCount);
        Assert.AreEqual(second.GroupId, status.VerifiedCutoff?.GroupId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LateComputeCopyRefreshesTheDependentReceiptAcrossWorkerRestart(bool unavailable)
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var (settings, store) = LocalStore();
        InstallBinding(settings, store);
        using var source = OpenLocal(settings, store, Path.Combine(silo.RootPath, "installed"), silo.Clock, silo.Payloads);
        await source.VerifyReadinessAsync(token).ConfigureAwait(false);
        var fixture = await SiloArchiveFixture.StartAsync(source.Binding.BindingId, token).ConfigureAwait(false);
        await using var lease = fixture.ConfigureAwait(false);
        fixture.Settings["ReadScratchRoot"] = silo.RootPath;
        var input = await PublishInstalledAsync(source, store, fixture, SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false), token,
            replicateMembers: false).ConfigureAwait(false);
        var member = input.Members.Single(member => !member.Required);
        fixture.Replicate(store, input.Members.Single(member => member.Required).Locator);
        var draft = SiloDrafts.Capture(silo.Payloads, 2) with
        {
            Dependencies = [new(member.ArtifactId, member.ArtifactRevision, member.Sha256, SiloDependencyKind.Compute, Required: true)]
        };
        var dependent = await PublishInstalledAsync(source, store, fixture, draft, token).ConfigureAwait(false);
        if (unavailable) fixture.UnavailableKey = member.Locator.Key;
        using var services = new ServiceCollection().AddSingleton(source).BuildServiceProvider();
        using var worker = new SiloArchiveWorker(fixture.Configuration, services, silo.Clock, NullLogger<SiloArchiveWorker>.Instance);
        await worker.RunCycleAsync(token).ConfigureAwait(false);
        await ReplicateJournalAsync(source, store, fixture, token).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromSeconds(2));
        var initial = await worker.RunCycleAsync(token).ConfigureAwait(false);
        var oldReceipt = initial.VerifiedCutoff;
        Assert.AreEqual(dependent.GroupId, oldReceipt?.GroupId);
        Assert.IsNotNull(oldReceipt);
        using (var missing = await worker.ReadAsync(new(oldReceipt, member.ArtifactId, member.ArtifactRevision), token).ConfigureAwait(false))
            Assert.AreEqual(unavailable ? "blocked" : "missing", missing.State);

        fixture.UnavailableKey = null;
        fixture.Replicate(store, member.Locator);
        silo.Clock.Advance(TimeSpan.FromDays(1));
        var payloadKey = dependent.Members[0].Locator.Key;
        var payloadGets = fixture.Gets(payloadKey);
        var catchingUp = await worker.RunCycleAsync(token).ConfigureAwait(false);
        Assert.AreEqual(oldReceipt.ReceiptSha256, catchingUp.VerifiedCutoff?.ReceiptSha256);
        Assert.AreEqual(0L, catchingUp.BacklogCount);
        Assert.AreEqual(payloadGets + 1, fixture.Gets(payloadKey));
        Assert.AreEqual((1, SiloSqlite.Ticks(silo.Clock.GetUtcNow().AddSeconds(2))), await ReadRetryAsync().ConfigureAwait(false));
        var head = await source.State.ReadHeadAsync(token).ConfigureAwait(false);

        silo.Clock.Advance(TimeSpan.FromSeconds(2));
        var waiting = await worker.RunCycleAsync(token).ConfigureAwait(false);
        Assert.AreEqual(oldReceipt.ReceiptSha256, waiting.VerifiedCutoff?.ReceiptSha256);
        Assert.AreEqual(payloadGets + 2, fixture.Gets(payloadKey));
        Assert.AreEqual((2, SiloSqlite.Ticks(silo.Clock.GetUtcNow().AddSeconds(4))), await ReadRetryAsync().ConfigureAwait(false));
        await ReplicateJournalAsync(source, store, fixture, token).ConfigureAwait(false);

        using var restarted = new SiloArchiveWorker(fixture.Configuration, services, silo.Clock, NullLogger<SiloArchiveWorker>.Instance);
        silo.Clock.Advance(TimeSpan.FromSeconds(2));
        var prefixAdvanced = await restarted.RunCycleAsync(token).ConfigureAwait(false);
        Assert.AreEqual(oldReceipt.ReceiptSha256, prefixAdvanced.VerifiedCutoff?.ReceiptSha256);
        Assert.AreEqual(payloadGets + 2, fixture.Gets(payloadKey), "The worker should wait until the persisted due time.");
        silo.Clock.Advance(TimeSpan.FromSeconds(2));
        var caughtUp = await restarted.RunCycleAsync(token).ConfigureAwait(false);
        var lookup = await restarted.FindAsync(dependent.GroupId, dependent.RevisionId, token).ConfigureAwait(false);
        var newReceipt = lookup?.Receipt;
        Assert.IsNotNull(newReceipt);
        Assert.AreNotEqual(oldReceipt.ReceiptSha256, newReceipt.ReceiptSha256);
        Assert.AreEqual(newReceipt, caughtUp.VerifiedCutoff);
        Assert.AreEqual(payloadGets + 3, fixture.Gets(payloadKey));
        Assert.AreEqual((0, SiloSqlite.Ticks(silo.Clock.GetUtcNow().AddDays(1))), await ReadRetryAsync().ConfigureAwait(false));
        Assert.AreEqual(head, await source.State.ReadHeadAsync(token).ConfigureAwait(false), "A restart must reuse the committed refresh.");
        using (var reachable = await restarted.ReadAsync(new(newReceipt, member.ArtifactId, member.ArtifactRevision), token).ConfigureAwait(false))
        {
            Assert.AreEqual("reachable", reachable.State);
            Assert.AreEqual(member.Sha256, reachable.Sha256);
        }
        using var original = await restarted.ReadAsync(new(oldReceipt, member.ArtifactId, member.ArtifactRevision), token).ConfigureAwait(false);
        Assert.AreEqual(unavailable ? "blocked" : "missing", original.State, "Previously issued receipt IDs remain immutable.");

        async Task<(int Attempts, long Next)> ReadRetryAsync()
        {
            using var connection = await SiloSqlite.OpenAsync(source.PhysicalState.Resolve(fixture.Marker.StoreId + ".db"), token).ConfigureAwait(false);
            using var command = SiloSqlite.CreateCommand(connection, null,
                "SELECT attempts, next_ticks FROM archive_work WHERE group_id = $group;", ("$group", dependent.GroupId));
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync(token).ConfigureAwait(false));
            return (reader.GetInt32(0), reader.GetInt64(1));
        }
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
        await ownCatalog.CompleteAttemptAsync(work, "reachable", null, now, commit, now, resetAttempts: true, token).ConfigureAwait(false);
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

    private static SiloInstalledStorage OpenLocal(SiloInstalledSettings settings, SiloTestStore store, string root, TimeProvider clock,
        SiloTestPayloads? payloads = null) =>
        new(settings, Options.Create(new CameraAgentHostOptions { RawIngressRoot = root }), clock, faults: null, decorateReadStore: null,
            new SiloInstalledStore(settings, store, _ => ValueTask.FromResult(true)), payloads ?? new SiloTestPayloads());

    private static async Task<SiloPublishRecord> PublishInstalledAsync(SiloInstalledStorage source, SiloTestStore store,
        SiloArchiveFixture fixture, SiloGroupDraft draft, CancellationToken token, bool replicateMembers = true)
    {
        await source.Publisher.StageAsync(draft, token).ConfigureAwait(false);
        var outcome = await source.Publisher.PublishAsync(draft.GroupId, draft.RevisionId, token).ConfigureAwait(false);
        Assert.AreEqual(SiloStagedState.Visible, outcome.State);
        var head = await source.State.ReadHeadAsync(token).ConfigureAwait(false);
        var commit = await source.State.ReadCommittedSegmentAsync(new(head.Generation, head.Sequence), token).ConfigureAwait(false);
        Assert.IsNotNull(commit);
        var bytes = store.Read(SiloObjectNamespace.SegmentKey(commit.Position), commit.VersionId);
        var publish = SiloJson.ReadSegment(bytes, "fixture publication").Records.OfType<SiloPublishRecord>().Single();
        fixture.Replicate(store, publish.Manifest.Locator);
        if (replicateMembers)
        {
            foreach (var member in publish.Members)
                fixture.Replicate(store, member.Locator);
        }
        await ReplicateJournalAsync(source, store, fixture, token).ConfigureAwait(false);
        return publish;
    }

    private static async Task ReplicateJournalAsync(SiloInstalledStorage source, SiloTestStore store,
        SiloArchiveFixture fixture, CancellationToken token)
    {
        var head = await source.State.ReadHeadAsync(token).ConfigureAwait(false);
        for (var sequence = 1L; sequence <= head.Sequence; sequence++)
        {
            var commit = await source.State.ReadCommittedSegmentAsync(new(head.Generation, sequence), token).ConfigureAwait(false);
            Assert.IsNotNull(commit);
            fixture.Replicate(store, commit);
        }
    }

    private static async Task JournalLocalExpiryAsync(SiloInstalledStorage source, SiloTestStore store,
        SiloObjectIdentity identity, SiloExactLocator locator, DateTimeOffset now, CancellationToken token)
    {
        await source.ArchiveJournal.AppendAsync(
            [new SiloExpiryIntentRecord("local-expiry", SiloExpiryScope.Role, identity, "last-copy-policy:operator",
                [new(locator, "local")], "retention elapsed", LastCopy: true)], "local-expiry-intent", token).ConfigureAwait(false);
        store.Delete(locator);
        await source.ArchiveJournal.AppendAsync(
            [new SiloExpiryDispositionRecord("local-expiry", locator, SiloExpiryOutcome.Deleted, now, "NoSuchVersion"),
                new SiloAvailabilityRecord(SiloAvailabilityScope.Role, identity, SiloAvailabilityState.PolicyExpired,
                    now, "last copy expired", IntentId: "local-expiry")], "local-expiry-settlement", token).ConfigureAwait(false);
        await source.Catalog.CatchUpAsync(await source.State.ReadWriterEvidenceAsync(token).ConfigureAwait(false), null, token).ConfigureAwait(false);
        Assert.AreEqual(SiloDisposition.PolicyExpired, (await source.Index.ReadRoleAsync(identity.Role, token).ConfigureAwait(false))?.Disposition);
    }
}
