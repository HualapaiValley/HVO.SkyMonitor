using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Tests.TimeLapses;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Silo")]
public sealed partial class SiloRetentionTests
{
    private static readonly SiloPhysicalHeadroom Healthy = new(100 * SiloDiskBudget.GiB, 20 * SiloDiskBudget.GiB, 0);
    private static readonly SiloRetentionPolicy Retention = new(false, [new("raw", TimeSpan.FromDays(1), true, true)]);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void PhysicalPressureLatchesAndPreservesRestoreCaptureCatalogAndCleanupReservations()
    {
        var physical = Healthy;
        var budget = new SiloDiskBudget(() => physical, 2 * SiloDiskBudget.GiB, SiloDiskBudget.GiB,
            SiloDiskBudget.GiB, SiloDiskBudget.GiB);
        Assert.AreEqual(15 * SiloDiskBudget.GiB, SiloDiskBudget.InstallerReserve(300 * SiloDiskBudget.GiB));
        Assert.AreEqual(Healthy, budget.Observe());
        using var restore = budget.TryReserve(SiloSpacePurpose.Restore, 5 * SiloDiskBudget.GiB);
        Assert.IsNotNull(restore);
        Assert.IsNull(budget.TryReserve(SiloSpacePurpose.Restore, 2 * SiloDiskBudget.GiB));
        physical = physical with { AvailableBytes = 12 * SiloDiskBudget.GiB };
        Assert.IsNull(budget.TryReserve(SiloSpacePurpose.Capture, 1));
        var paused = budget.AdmissionRelease;
        Assert.IsNotNull(paused);
        using var catalog = budget.TryReserve(SiloSpacePurpose.Catalog, 1);
        using var cleanup = budget.TryReserve(SiloSpacePurpose.Cleanup, 1);
        Assert.IsNotNull(catalog);
        Assert.IsNotNull(cleanup);
        restore.Dispose();
        physical = physical with { AvailableBytes = 13 * SiloDiskBudget.GiB };
        budget.Observe();
        Assert.IsFalse(paused.IsCompleted);
        physical = Healthy;
        budget.Observe();
        Assert.IsTrue(paused.IsCompletedSuccessfully);
        Assert.IsNull(budget.AdmissionRelease);
    }

    [TestMethod]
    public async Task PolicyPinsAndEveryHoldProtectSharedBytesUntilTheirFinalRelease()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var store = new TestRemoval(silo.Local);
        var retention = Open(silo, store);
        var (raw, second, preview) = await PublishSharedAsync(silo, token).ConfigureAwait(false);
        Assert.AreEqual("held", await SweepAsync(retention, token).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => retention.SetPolicyAsync(new(false,
            [new("raw", null, true, true), new("raw", null, true, true)]), token).AsTask()).ConfigureAwait(false);
        await retention.SetPolicyAsync(Retention, token).ConfigureAwait(false);
        await retention.SetPinAsync("raw-pin", raw.Identity.Role, true, "operator", token).ConfigureAwait(false);
        var now = silo.Clock.GetUtcNow().AddDays(2);
        Assert.AreEqual("held-reference", (await PlanAsync(retention, raw.Locator, now, token).ConfigureAwait(false)).Decision);
        await silo.RestartAsync().ConfigureAwait(false);
        retention = Open(silo, store);
        Assert.AreEqual("held-reference", (await PlanAsync(retention, raw.Locator, now, token).ConfigureAwait(false)).Decision);
        await retention.SetPinAsync("raw-pin", raw.Identity.Role, false, "released", token).ConfigureAwait(false);
        foreach (var kind in Enum.GetValues<SiloHoldKind>().Where(static kind => kind != SiloHoldKind.SatelliteArchiveReference))
        {
            var first = await retention.DryRunAsync(now, false,
                [new(kind, second.Identity.Id, second.Identity.Revision)], token).ConfigureAwait(false);
            Assert.AreEqual("held-reference", first.Single(candidate => candidate.Locator == raw.Locator).Decision, kind.ToString());
        }
        var active = await retention.TryAcquireAsync([second.Locator], TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
        Assert.IsNotNull(active);
        await silo.RestartAsync().ConfigureAwait(false);
        retention = Open(silo, store);
        Assert.IsTrue(await retention.RevalidateAsync(active, token).ConfigureAwait(false));
        Assert.AreEqual("held-reference", (await PlanAsync(retention, raw.Locator, silo.Clock.GetUtcNow(), token).ConfigureAwait(false)).Decision);
        await retention.ReleaseAsync(active, token).ConfigureAwait(false);

        var one = await retention.DryRunAsync(now, false, [], token).ConfigureAwait(false);
        var two = await retention.DryRunAsync(now, false, [], token).ConfigureAwait(false);
        CollectionAssert.AreEqual(SiloJson.Serialize(one), SiloJson.Serialize(two));
        Assert.AreEqual("expire-role", one.Single(candidate => candidate.Locator == raw.Locator).Decision);
        Assert.AreEqual("within-policy", one.Single(candidate => candidate.Locator == preview.Locator).Decision);
        await retention.SetPolicyAsync(new(false, [new("raw", null, true, false)]), token).ConfigureAwait(false);
        var pressure = await retention.DryRunAsync(now, true, [], token).ConfigureAwait(false);
        Assert.AreEqual("last-copy-policy-or-survivor-required", pressure.Single(candidate => candidate.Locator == raw.Locator).Decision);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UncertainDeleteSettlesOnlyAfterExactAbsenceOrVerifiedExitAcrossRestart(bool removed)
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var store = new TestRemoval(silo.Local) { LoseDeleteReply = true, Remove = removed };
        var physical = Healthy;
        var budget = new SiloDiskBudget(() => physical, 1, 1, 1, 1, measureAllocation: _ => 8192);
        var retention = Open(silo, store, budget);
        var (first, second, preview) = await PublishSharedAsync(silo, token).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromMinutes(5));
        await retention.SetPolicyAsync(new(false, [new("raw", TimeSpan.Zero, false, true)]), token).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<SiloStoreUnavailableException>(() => SweepAsync(retention, token).AsTask()).ConfigureAwait(false);
        Assert.AreEqual(1, store.Deletes);

        await silo.RestartAsync().ConfigureAwait(false);
        retention = Open(silo, store, budget);
        Assert.IsNull(await retention.TryAcquireAsync([first.Locator], TimeSpan.FromMinutes(1), token).ConfigureAwait(false));
        Assert.AreEqual(removed ? "already-absent" : "unsettled-delete", await SweepAsync(retention, token).ConfigureAwait(false));
        if (!removed)
        {
            Assert.AreEqual("not-deleted", await retention.SweepAsync(false, NoHolds, null, token, verifiedStoreExit: _ => true).ConfigureAwait(false));
            Assert.AreNotEqual("policy-expired", (await silo.Index.ReadRoleAsync(first.Identity.Role, token).ConfigureAwait(false))!.Disposition);
            Assert.IsNull(SiloRetentionState.Read(silo.StateRoot.Root).ReclaimLedger);
        }
        else
        {
            var charge = SiloRetentionState.Read(silo.StateRoot.Root).ReclaimLedger?.Entries.Single().Allocation;
            Assert.AreEqual(8192L, charge?.Bytes);
            Assert.HasCount(1, silo.JournalRecords().OfType<SiloExpiryIntentRecord>().SelectMany(static intent => intent.Targets)
                .Where(static target => target.AllocationId is not null).ToArray());
            physical = physical with { TrashBytes = 8192 };
            silo.Clock.Advance(TimeSpan.FromMinutes(10));
            Assert.AreEqual("awaiting-physical-reclamation", await SweepAsync(retention, token).ConfigureAwait(false));
            await silo.RestartAsync().ConfigureAwait(false);
            retention = Open(silo, store, budget);
            physical = physical with { AvailableBytes = Healthy.AvailableBytes + 4096 };
            Assert.AreEqual("awaiting-physical-reclamation", await SweepAsync(retention, token).ConfigureAwait(false));
            physical = physical with { TrashBytes = 0 };
            Assert.AreEqual("no-eligible-versions", await SweepAsync(retention, token).ConfigureAwait(false));
            await silo.DeleteCatalogAndRebuildAsync().ConfigureAwait(false);
            await silo.Catalog.VerifyRolesAsync(32, token).ConfigureAwait(false);
            foreach (var raw in new[] { first, second })
                Assert.AreEqual("policy-expired", (await silo.Index.ReadRoleAsync(raw.Identity.Role, token).ConfigureAwait(false))!.Disposition);
            Assert.AreEqual("available", (await silo.Index.ReadRoleAsync(preview.Identity.Role, token).ConfigureAwait(false))!.Disposition);
            Assert.HasCount(2, silo.JournalRecords().OfType<SiloExpiryDispositionRecord>().ToArray());
        }
        Assert.AreEqual(1, store.Deletes);
    }

    [TestMethod]
    public async Task ArchiveOutageRequiresExplicitRoleAuthorityAndSettlesOnlyAfterCustodyAbsence()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false);
        await silo.PublishAsync(draft).ConfigureAwait(false);
        var published = silo.PublishRecord(draft.GroupId);
        var raw = SiloHarness.Identity(published, SiloDrafts.Raw(1));
        var local = silo.MemberLocator(draft.GroupId, raw.Id);
        var survivor = silo.ReplicateToArchive(raw, local);
        await silo.AppendAsync(survivor).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromDays(2));
        var physical = Healthy;
        var store = new TestRemoval(silo.Local);
        var budget = Budget(() => physical);
        var retention = Open(silo, store, budget);
        await retention.SetPolicyAsync(new(false, [new("raw", TimeSpan.Zero, true, false)]), token).ConfigureAwait(false);
        Assert.AreEqual("no-eligible-versions", await SweepAsync(retention, token).ConfigureAwait(false));
        Assert.AreEqual(0, store.Deletes);

        await retention.SetPolicyAsync(Retention, token).ConfigureAwait(false);
        Assert.AreEqual("deleted", await SweepAsync(retention, token).ConfigureAwait(false));
        Assert.AreEqual(1, store.Deletes);
        Assert.IsNull((await silo.Index.ReadRoleAsync(raw.Role, token).ConfigureAwait(false))!.Journaled);
        var localOnly = new SiloCatalog(silo.Index, new SiloStoreSet(silo.Local), SiloNoAuthorityEvidence.Instance);
        await localOnly.VerifyRolesAsync(32, token, includeAvailable: true).ConfigureAwait(false);
        Assert.AreEqual("temporarily-unavailable", (await silo.Index.ReadRoleAsync(raw.Role, token).ConfigureAwait(false))!.Disposition);

        await silo.RestartAsync().ConfigureAwait(false);
        retention = Open(silo, store, budget);
        silo.Archive.Delete(survivor.Locator);
        physical = physical with { AvailableBytes = physical.AvailableBytes + 8192 };
        var custody = new ExpiredArchive(survivor.Locator);
        await retention.ReconcileArchiveAsync(custody, token).ConfigureAwait(false);
        Assert.AreEqual("policy-expired", (await silo.Index.ReadRoleAsync(raw.Role, token).ConfigureAwait(false))!.Journaled);
        Assert.HasCount(1, silo.JournalRecords().OfType<SiloAvailabilityRecord>().ToArray());
    }

    private sealed class ExpiredArchive(SiloExactLocator expired) : ISiloRetentionArchive
    {
        public ValueTask<bool> ProtectAsync(SiloObjectIdentity identity, SiloExactLocator survivor, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> ConfirmProtectedAsync(SiloObjectIdentity identity, SiloExactLocator survivor, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);

        public ValueTask<bool> IsAuthorityExpiredAsync(SiloExactLocator locator, CancellationToken cancellationToken) =>
            ValueTask.FromResult(locator == expired);
    }

    [TestMethod]
    public async Task ArchiveOutageAfterSettledEvictionDoesNotFenceLocalPublicationAcrossRestart()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false);
        await silo.PublishAsync(draft).ConfigureAwait(false);
        var raw = SiloHarness.Identity(silo.PublishRecord(draft.GroupId), SiloDrafts.Raw(1));
        var local = silo.MemberLocator(draft.GroupId, raw.Id);
        var survivor = silo.ReplicateToArchive(raw, local);
        await silo.AppendAsync(survivor).ConfigureAwait(false);

        var store = new TestRemoval(silo.Local);
        var retention = Open(silo, store);
        silo.Clock.Advance(TimeSpan.FromDays(2));
        await retention.SetPolicyAsync(new(false, [new("raw", TimeSpan.Zero, true, true)]), token).ConfigureAwait(false);
        var custody = new InterruptedSurvivorVerification(store);
        Assert.AreEqual("deleted",
            await retention.SweepAsync(true, NoHolds, custody, token).ConfigureAwait(false));
        Assert.IsNull(retention.ReadUnsettledTarget());
        Assert.IsNotNull(SiloRetentionState.Read(silo.StateRoot.Root).ReclaimLedger);
        await retention.ReconcileArchiveAsync(custody, token).ConfigureAwait(false);
        Assert.IsEmpty(silo.JournalRecords().OfType<SiloReleaseSurvivorRecord>());

        await silo.RestartAsync().ConfigureAwait(false);
        retention = Open(silo, store);
        silo.Publisher.RetentionFence = retention.ReadUnsettledTarget;
        var next = SiloDrafts.Capture(silo.Payloads, 2, rawRequired: false);
        await silo.PublishAsync(next).ConfigureAwait(false);
        Assert.AreEqual(next.GroupId, silo.PublishRecord(next.GroupId).GroupId);
        Assert.HasCount(1, SiloRetentionState.Read(silo.StateRoot.Root).SurvivorReleases);
        Assert.AreEqual("deleted", await retention.SweepAsync(true, NoHolds, null, token).ConfigureAwait(false));
        Assert.HasCount(1, SiloRetentionState.Read(silo.StateRoot.Root).SurvivorReleases);

        custody.Reachable = true;
        await retention.ReconcileArchiveAsync(custody, token).ConfigureAwait(false);
        Assert.IsEmpty(SiloRetentionState.Read(silo.StateRoot.Root).SurvivorReleases);
        Assert.HasCount(1, silo.JournalRecords().OfType<SiloReleaseSurvivorRecord>());
        Assert.HasCount(2, silo.JournalRecords().OfType<SiloExpiryDispositionRecord>());
        Assert.AreEqual(2, store.Deletes);
    }

    private sealed class InterruptedSurvivorVerification(TestRemoval local) : ISiloRetentionArchive
    {
        public bool Reachable { get; set; }

        public ValueTask<bool> ProtectAsync(SiloObjectIdentity identity, SiloExactLocator survivor, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> ConfirmProtectedAsync(SiloObjectIdentity identity, SiloExactLocator survivor, CancellationToken cancellationToken) =>
            ValueTask.FromResult(local.Deletes == 0 || Reachable);
    }

    [TestMethod]
    public async Task SatelliteRawStaysHeldByTheLastArchivedCapturePayloadWithoutASupportProduct()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var draft = SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false);
        await silo.PublishAsync(draft).ConfigureAwait(false);
        var published = silo.PublishRecord(draft.GroupId);
        var raw = SiloHarness.Identity(published, SiloDrafts.Raw(1));
        var preview = SiloHarness.Identity(published, SiloDrafts.Preview(1));
        var rawLocator = silo.MemberLocator(draft.GroupId, raw.Id);
        var archive = silo.ReplicateToArchive(preview, silo.MemberLocator(draft.GroupId, preview.Id));
        await silo.AppendAsync(archive).ConfigureAwait(false);
        var retention = OpenForFixture(silo);
        await retention.SetPolicyAsync(Retention, token).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromDays(2));
        var now = silo.Clock.GetUtcNow();
        SiloRetentionHold[] holds = [new(SiloHoldKind.SatelliteArchiveReference, raw.Id, raw.Revision)];
        var first = await retention.DryRunAsync(now, false, holds, token).ConfigureAwait(false);
        Assert.AreEqual("held-reference", first.Single(item => item.Locator == rawLocator).Decision);

        silo.Archive.Delete(archive.Locator);
        await silo.AppendAsync(new SiloExpiryIntentRecord("archive-preview", SiloExpiryScope.Copy, preview, "archive-custody",
            [new(archive.Locator, "archive")], "archive-operator-expiry", false),
            new SiloExpiryDispositionRecord("archive-preview", archive.Locator, SiloExpiryOutcome.Deleted, now, "exact-version-absent"))
            .ConfigureAwait(false);
        var released = await retention.DryRunAsync(now, false, holds, token).ConfigureAwait(false);
        Assert.AreEqual("expire-role", released.Single(item => item.Locator == rawLocator).Decision);
    }

    [TestMethod]
    public async Task FrozenVideoInputStaysHeldAcrossWorkerRestartUntilTheJobIsTerminal()
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        byte[] bytes = [1, 0, 2, 0, 3, 0, 4, 0];
        var descriptor = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, bytes).Descriptor;
        var artifactId = descriptor.Artifact.ArtifactId.ToString("D");
        var revision = CaptureContractJson.ComputeDescriptorSha256(descriptor);
        var draft = SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false);
        draft = draft with
        {
            Members =
            [
                SiloDrafts.Member(silo.Payloads, artifactId, "raw", SiloRetentionClass.Full,
                    "application/octet-stream", bytes, required: false, revision: revision),
                draft.Members[1]
            ]
        };
        await silo.PublishAsync(draft).ConfigureAwait(false);
        var physical = silo.MemberLocator(draft.GroupId, artifactId);
        silo.Clock.Advance(TimeSpan.FromDays(2));
        var retention = OpenForFixture(silo);
        await retention.SetPolicyAsync(Retention, token).ConfigureAwait(false);

        var options = SqliteTimeLapseStoreTests.Options(silo.RootPath);
        var videoClock = new NightlyClock(NightlyProductFixture.DayEndUtc.AddMinutes(10));
        var occurrence = SqliteTimeLapseStoreTests.Occurrence(options.Value.TimeLapses, LocalAutomationSourceWindowKind.CompletedCivilHour);
        using (var video = new SqliteTimeLapseStore(options, videoClock))
        {
            using var worker = await video.AcquireWorkerAsync(token).ConfigureAwait(false);
            await video.EnqueueAsync(occurrence, options.Value.TimeLapses.Freeze(NightlyProductFixture.Configuration()), [], token)
                .ConfigureAwait(false);
            var running = await video.ClaimAsync(token).ConfigureAwait(false);
            Assert.IsNotNull(running);
            await video.FreezePlanAsync(running.JobId,
                new([new(descriptor, "raw/frame.bin", "raw/frame.json")], new Dictionary<string, int>()), token).ConfigureAwait(false);
            Assert.AreEqual("no-eligible-versions",
                await retention.SweepAsync(true, video.ReadSiloTimeLapseHoldsAsync, null, token).ConfigureAwait(false));
        }
        await silo.RestartAsync().ConfigureAwait(false);
        retention = OpenForFixture(silo);
        using var replacement = new SqliteTimeLapseStore(options, videoClock);
        using var replacementWorker = await replacement.AcquireWorkerAsync(token).ConfigureAwait(false);
        var holdsCaptured = new TaskCompletionSource<IReadOnlyList<SiloRetentionHold>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweep = retention.SweepAsync(true, async ct =>
        {
            var holds = await replacement.ReadSiloTimeLapseHoldsAsync(ct).ConfigureAwait(false);
            holdsCaptured.SetResult(holds);
            await completed.Task.WaitAsync(ct).ConfigureAwait(false);
            return holds;
        }, null, token).AsTask();
        try
        {
            var holds = await holdsCaptured.Task.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            Assert.IsTrue(holds.Any(hold => hold.Kind == SiloHoldKind.TimeLapseInput && hold.ArtifactId == artifactId && hold.Revision == revision));
            var resumed = await replacement.ClaimAsync(token).ConfigureAwait(false);
            Assert.IsNotNull(resumed);
            await replacement.CompleteAsync(resumed.JobId, CameraAgentTimeLapseState.Failed, "fixture-terminal", null, token).ConfigureAwait(false);
        }
        finally
        {
            completed.SetResult();
        }
        Assert.AreEqual("no-eligible-versions", await sweep.ConfigureAwait(false));
        Assert.AreEqual("expire-role", (await PlanAsync(retention, physical, silo.Clock.GetUtcNow(), token).ConfigureAwait(false)).Decision);
        Assert.AreEqual("deleted", await retention.SweepAsync(true, replacement.ReadSiloTimeLapseHoldsAsync, null, token).ConfigureAwait(false));
    }

    internal static SiloRetention OpenForFixture(SiloHarness silo) => Open(silo, new TestRemoval(silo.Local));

    private static SiloRetention Open(SiloHarness silo, TestRemoval store, ISiloDiskBudget? budget = null) =>
        new(silo.State, silo.Index, silo.Publisher, silo.Catalog, silo.Journal, silo.StateRoot.Root, store,
            budget ?? Budget(() => Healthy), silo.Clock);

    private static SiloDiskBudget Budget(Func<SiloPhysicalHeadroom> observe) => new(observe, 1, 1, 1, 1);

    private static ValueTask<IReadOnlyList<SiloRetentionHold>> NoHolds(CancellationToken _) =>
        ValueTask.FromResult<IReadOnlyList<SiloRetentionHold>>([]);

    private static ValueTask<string> SweepAsync(SiloRetention retention, CancellationToken token) => retention.SweepAsync(true, NoHolds, null, token);

    private static async ValueTask<SiloRetentionCandidate> PlanAsync(SiloRetention retention, SiloExactLocator locator, DateTimeOffset now,
        CancellationToken token) =>
        (await retention.DryRunAsync(now, false, [], token).ConfigureAwait(false)).Single(candidate => candidate.Locator == locator);

    private static async ValueTask<(SiloCopyRecord First, SiloCopyRecord Second, SiloCopyRecord Preview)> PublishSharedAsync(
        SiloHarness silo, CancellationToken token)
    {
        var one = SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false);
        var two = SiloDrafts.Capture(silo.Payloads, 2, rawRequired: false);
        two = two with { Members = [one.Members[0] with { ArtifactId = two.Members[0].ArtifactId }, two.Members[1]] };
        await silo.PublishAsync(one).ConfigureAwait(false);
        await silo.PublishAsync(two).ConfigureAwait(false);
        async ValueTask<SiloCopyRecord> GetAsync(SiloDraftMember member)
        {
            var role = (await silo.Index.ReadRoleAsync(new(SiloIdentityKind.Member, member.ArtifactId, member.ArtifactRevision), token)
                .ConfigureAwait(false))!;
            return new(new(role.Role.Kind, role.Role.Id, role.Role.Revision, role.Sha256, role.Length), role.Locators.Single().Locator,
                SiloStoreClass.Local, SiloRetrieval.Online, "test", silo.Clock.GetUtcNow(), SiloFormats.ExactGetSha256);
        }
        var first = await GetAsync(one.Members[0]).ConfigureAwait(false);
        var second = await GetAsync(two.Members[0]).ConfigureAwait(false);
        Assert.AreEqual(first.Locator, second.Locator);
        return (first, second, await GetAsync(one.Members[1]).ConfigureAwait(false));
    }

    internal sealed class TestRemoval(SiloTestStore store) : ISiloObjectStore, ISiloVersionRemoval
    {
        private readonly HashSet<SiloExactLocator> _removed = [];
        public bool LoseDeleteReply { get; init; }
        public bool Remove { get; init; } = true;
        public int Deletes { get; private set; }
        public Func<CancellationToken, Task>? BeforeDelete { get; set; }
        public Action? Deleted { get; init; }
        public SiloStoreDescriptor Descriptor => store.Descriptor;

        public async ValueTask<bool> DeleteVersionAsync(SiloExactLocator locator, CancellationToken cancellationToken)
        {
            if (BeforeDelete is not null) await BeforeDelete(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Deletes++;
            if (Remove)
            {
                store.Delete(locator);
                _removed.Add(locator);
            }
            Deleted?.Invoke();
            if (LoseDeleteReply) throw new SiloStoreUnavailableException(Descriptor.StoreId, "delete", writeMayHaveLanded: true);
            return true;
        }

        public ValueTask<SiloOpenResult> OpenVersionAsync(string key, string versionId, CancellationToken cancellationToken) =>
            _removed.Contains(new(Descriptor.StoreId, Descriptor.Bucket, key, versionId))
                ? ValueTask.FromResult(new SiloOpenResult(SiloOpenStatus.NoSuchVersion, null))
                : store.OpenVersionAsync(key, versionId, cancellationToken);

        public ValueTask<SiloPutResult> PutCreateOnlyAsync(SiloPutRequest request, CancellationToken cancellationToken) =>
            store.PutCreateOnlyAsync(request, cancellationToken);

        public ValueTask<string?> LocateCurrentVersionAsync(string key, CancellationToken cancellationToken) =>
            store.LocateCurrentVersionAsync(key, cancellationToken);

        public IAsyncEnumerable<string> ListKeysAsync(string prefix, string? startAfter, CancellationToken cancellationToken) =>
            store.ListKeysAsync(prefix, startAfter, cancellationToken);
    }
}
