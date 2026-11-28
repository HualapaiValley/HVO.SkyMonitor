using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

public sealed partial class SiloRetentionTests
{
    [TestMethod]
    [DataRow(20)]
    [DataRow(-20)]
    [DataRow(0)]
    public async Task ReclaimRebasesAcrossResizeOrSameSizeRootChangeAndPersistsPendingCharges(int capacityChangeGiB)
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, 1, rawRequired: false)).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromMinutes(2));

        const long allocation = 8192;
        var physical = Healthy with { TrashBytes = null, ReclaimRoot = new(8, 1, 20, TracksTrash: false) };
        var budget = new SiloDiskBudget(() => physical, 1, 1, 1, 1, measureAllocation: _ => allocation);
        var removal = new TestRemoval(silo.Local);
        var retention = Open(silo, removal, budget);
        await retention.SetPolicyAsync(new(false, [new("raw", TimeSpan.Zero, true, true)]), token).ConfigureAwait(false);
        Assert.AreEqual("deleted", await SweepAsync(retention, token).ConfigureAwait(false));
        var pending = SiloRetentionState.Read(silo.StateRoot.Root).ReclaimLedger!.Entries.Single();

        physical = physical with
        {
            TotalBytes = physical.TotalBytes + capacityChangeGiB * SiloDiskBudget.GiB,
            AvailableBytes = physical.AvailableBytes + SiloDiskBudget.GiB,
            ReclaimRoot = capacityChangeGiB == 0 ? new(8, 2, 20, TracksTrash: false) : physical.ReclaimRoot
        };
        await silo.RestartAsync().ConfigureAwait(false);
        retention = Open(silo, removal, budget);
        Assert.AreEqual("no-eligible-versions", await SweepAsync(retention, token).ConfigureAwait(false));
        var rebased = SiloRetentionState.Read(silo.StateRoot.Root).ReclaimLedger!;
        Assert.AreEqual(physical, rebased.Floor);
        Assert.AreEqual(pending, rebased.Entries.Single());
        Assert.AreEqual(0L, rebased.PartialDrainBytes);

        physical = physical with { AvailableBytes = physical.AvailableBytes + allocation };
        Assert.AreEqual("no-eligible-versions", await SweepAsync(retention, token).ConfigureAwait(false));
        Assert.IsNull(SiloRetentionState.Read(silo.StateRoot.Root).ReclaimLedger);
        Assert.AreEqual(1, removal.Deletes);
    }

    [TestMethod]
    [DataRow("resize")]
    [DataRow("new-root")]
    [DataRow("legacy")]
    public void ChangedCountersRequireFreshTrashEvidence(string change)
    {
        var prior = Healthy with { TrashBytes = 32768, ReclaimRoot = new(8, 1, 20, TracksTrash: true) };
        var current = change switch
        {
            "resize" => prior with { TrashBytes = 4096, TotalBytes = prior.TotalBytes + SiloDiskBudget.GiB },
            _ => prior with { TrashBytes = 4096, ReclaimRoot = new(8, 2, 20, TracksTrash: true) }
        };
        if (change == "legacy") prior = prior with { ReclaimRoot = null };
        var now = SiloHarness.Start;
        var pending = new SiloReclaimLedger([new("prior", new("prior-allocation", 8192))], prior, now, PartialDrainBytes: 4096);
        var rebased = SiloReclaimLedger.Observe(pending, current, now)!;
        Assert.AreEqual(pending.Entries.Single(), rebased.Entries.Single());
        Assert.AreEqual(current, rebased.Floor);
        Assert.AreEqual(0L, rebased.PartialDrainBytes);
        Assert.IsNull(SiloReclaimLedger.Observe(rebased, current with { TrashBytes = 0 }, now));
    }

    [TestMethod]
    public void TinyObjectMetadataDrainKeepsUnknownChargesUntilTrashIsEmpty()
    {
        var now = SiloHarness.Start;
        var before = Healthy with { TrashBytes = 24576 };
        var pending = new SiloReclaimLedger(
            [new("first", UpperBoundBytes: 4096), new("second", UpperBoundBytes: 4096)], before, now);
        var partiallyDrained = before with { TrashBytes = 8192 };
        var remaining = SiloReclaimLedger.Observe(pending, partiallyDrained, now.AddMinutes(1))!;
        CollectionAssert.AreEqual(pending.Entries.ToArray(), remaining.Entries.ToArray());
        Assert.AreEqual(now.AddMinutes(1), remaining.LastProgressUtc);
        Assert.IsNull(SiloReclaimLedger.Observe(remaining, partiallyDrained with { TrashBytes = 0 }, now.AddMinutes(2)));
    }

    [TestMethod]
    public void MeasuredAllocationRequiresEveryByteAndDoesNotCreditTheNextDelete()
    {
        var before = Healthy with { TrashBytes = null };
        var now = SiloHarness.Start;
        var prior = new SiloReclaimLedger([new("prior", new("prior-allocation", 8192))], before, now);
        var firstSample = before with { AvailableBytes = before.AvailableBytes + 8191 };
        var partial = SiloReclaimLedger.Observe(prior, firstSample, now);
        Assert.IsNotNull(partial);
        Assert.HasCount(1, partial.Entries);
        Assert.AreEqual(8191L, partial.PartialDrainBytes);

        var attempt = new SiloRetentionAttempt("next", new("local", "bucket", "key", "version"), [], [], before,
            ObservedUtc: now, Allocation: new("next-allocation", 8192), AfterDelete: firstSample);
        var result = SiloReclaimLedger.Add(prior, attempt, before with { AvailableBytes = before.AvailableBytes + 8192 }, now);
        Assert.IsNotNull(result);
        Assert.AreEqual("next", result.Entries.Single().AttemptId);
        Assert.AreEqual(0L, result.PartialDrainBytes);
    }

    [TestMethod]
    [DataRow(8192, 0)]
    [DataRow(7680, 0)]
    [DataRow(4097, 1)]
    [DataRow(1, 1)]
    public async Task DefaultBudgetSettlesBatchedUnknownAllocationsOnlyAfterPhysicalProgress(int gained, int pending)
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        foreach (var number in Enumerable.Range(1, 3))
            await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, number, rawRequired: false)).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromMinutes(2));

        var physical = Healthy with { TrashBytes = null };
        var budget = new SiloDiskBudget(() => physical, 1, 1, 1, 1, measureAllocation: null, measureTrash: null);
        var store = new TestRemoval(silo.Local);
        var retention = Open(silo, store, budget);
        await retention.SetPolicyAsync(new(false, [new("raw", TimeSpan.Zero, true, true)]), token).ConfigureAwait(false);
        Assert.AreEqual("deleted", await SweepAsync(retention, token).ConfigureAwait(false));
        Assert.AreEqual("deleted", await SweepAsync(retention, token).ConfigureAwait(false));
        await silo.RestartAsync().ConfigureAwait(false);
        retention = Open(silo, store, budget);
        var ledger = SiloRetentionState.Read(silo.StateRoot.Root).ReclaimLedger!;
        Assert.HasCount(2, ledger.Entries);
        Assert.IsTrue(ledger.Entries.All(static entry => entry.Allocation is null && entry.UpperBoundBytes == 4096));

        silo.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.AreEqual("awaiting-physical-reclamation", await SweepAsync(retention, token).ConfigureAwait(false));
        Assert.AreEqual(2, store.Deletes);

        physical = physical with { AvailableBytes = physical.AvailableBytes + gained };
        Assert.AreEqual("no-eligible-versions", await retention.SweepAsync(true, NoHolds, null, token,
            select: static _ => false).ConfigureAwait(false));
        ledger = SiloRetentionState.Read(silo.StateRoot.Root).ReclaimLedger;
        Assert.AreEqual(pending, ledger?.Entries.Count ?? 0);
        if (ledger is not null) Assert.AreEqual(silo.Clock.GetUtcNow(), ledger.LastProgressUtc);

        Assert.AreEqual("deleted", await SweepAsync(retention, token).ConfigureAwait(false));
        Assert.AreEqual(3, store.Deletes);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SeparateTrashObservationPreservesInFlightDrainsDespiteCaptureGrowth(bool fullyDrained)
    {
        var token = TestContext.CancellationToken;
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        foreach (var number in Enumerable.Range(1, 3))
            await silo.PublishAsync(SiloDrafts.Capture(silo.Payloads, number, rawRequired: false)).ConfigureAwait(false);
        silo.Clock.Advance(TimeSpan.FromMinutes(2));

        var physical = Healthy with { TrashBytes = null };
        long trash = 0;
        var samplesAfterDelete = 0;
        var budget = new SiloDiskBudget(() => physical, 1, 1, 1, 1, measureTrash: () =>
        {
            // Only the reclamation observation at FinishAsync sees complete drainage.
            if (samplesAfterDelete > 0 && ++samplesAfterDelete == 3 && fullyDrained) trash = 0;
            return trash;
        });
        var store = new TestRemoval(silo.Local)
        {
            Deleted = () =>
            {
                physical = physical with { AvailableBytes = physical.AvailableBytes - 8192 };
                trash += 4096;
            }
        };
        var retention = Open(silo, store, budget);
        await retention.SetPolicyAsync(new(false, [new("raw", TimeSpan.Zero, true, true)]), token).ConfigureAwait(false);
        Assert.AreEqual("deleted", await SweepAsync(retention, token).ConfigureAwait(false));
        Assert.AreEqual("deleted", await SweepAsync(retention, token).ConfigureAwait(false));
        Assert.IsNull(budget.Observe()!.Value.TrashBytes);
        Assert.AreEqual(8192L, budget.ObserveReclamation()!.Value.TrashBytes);
        var before = SiloRetentionState.Read(silo.StateRoot.Root).ReclaimLedger!;
        Assert.HasCount(2, before.Entries);

        store.BeforeDelete = _ =>
        {
            if (fullyDrained)
                samplesAfterDelete = 1;
            else
                trash = 0; // The new delete leaves 4096 bytes by the first post-delete sample.
            silo.Clock.Advance(TimeSpan.FromMinutes(1));
            return Task.CompletedTask;
        };
        Assert.AreEqual("deleted", await SweepAsync(retention, token).ConfigureAwait(false));
        var after = SiloRetentionState.Read(silo.StateRoot.Root).ReclaimLedger;
        if (fullyDrained)
        {
            Assert.IsNull(after);
        }
        else
        {
            Assert.IsNotNull(after);
            Assert.HasCount(3, after.Entries);
            CollectionAssert.AreEqual(before.Entries.ToArray(), after.Entries.Take(2).ToArray());
            Assert.AreEqual(silo.Clock.GetUtcNow(), after.LastProgressUtc);
            Assert.AreEqual(4096L, after.Floor.TrashBytes);
            Assert.IsLessThan(before.Floor.AvailableBytes, after.Floor.AvailableBytes);
        }
    }
}
