using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

public sealed partial class SiloRetentionTests
{
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
            Assert.HasCount(2, after.Entries);
            Assert.AreEqual(before.Entries[1], after.Entries[0]);
            Assert.AreEqual(silo.Clock.GetUtcNow(), after.LastProgressUtc);
            Assert.AreEqual(4096L, after.Floor.TrashBytes);
            Assert.IsLessThan(before.Floor.AvailableBytes, after.Floor.AvailableBytes);
        }
    }
}
