using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Fetch;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Policy;

[TestClass]
[TestCategory("Unit")]
public sealed class SwpcResumeTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task EveryResumeMaximumTermWinsWithoutResettingBackoffOrCounters(int winner)
    {
        var now = SwpcTestData.Now;
        using var store = new SwpcPolicyTestStoreDouble();
        var checkpoint = new SwpcFetchCheckpoint(now.AddSeconds(winner == 1 ? 600 : -60),
            now.AddSeconds(winner == 2 ? 600 : -60), true, 5, 3, 4, now.AddDays(-1));
        var stored = store.View.Products[SwpcProduct.Alerts] with
        {
            ProductHalt = new("not-found", now.AddMinutes(-1)),
            LastAttemptUtc = now.AddSeconds(winner == 3 ? -10 : -120),
            Checkpoint = checkpoint
        };
        store.View = store.View with { Products = store.View.Products.SetItem(SwpcProduct.Alerts, stored) };
        using var runner = new SwpcAttemptRunner(store, new PolicyStubClient(), new ManualTimeProvider(now), SwpcJitter.None);
        var id = Guid.NewGuid();
        var result = await runner.ResumeAsync(SwpcProduct.Alerts, id).ConfigureAwait(false);
        Assert.IsFalse(result.NotHalted);
        var command = store.Halts.Single();
        Assert.AreEqual(id, command.CommandId);
        Assert.AreEqual(new SwpcHaltChange(SwpcProduct.Alerts, null), command.Change);
        Assert.AreEqual(1, command.Checkpoints.Count);
        Assert.AreEqual(checkpoint with { NextAttemptNotBeforeUtc = now.AddSeconds(winner == 0 ? 0 : winner == 3 ? 50 : 600), RetryNotBeforeUtc = null },
            result.View.Products[SwpcProduct.Alerts].Checkpoint);
        Assert.IsNull(result.View.Products[SwpcProduct.Alerts].ProductHalt);
    }

    [TestMethod]
    public void ProviderResumeCoversOnlyProductsWithoutRemainingProductHalt()
    {
        var now = SwpcTestData.Now;
        var view = SwpcPolicyTestStoreDouble.EmptyView();
        var alerts = view.Products[SwpcProduct.Alerts] with { ProductHalt = new("not-found", now) };
        view = view with { ProviderHalt = new("forbidden", now), Products = view.Products.SetItem(SwpcProduct.Alerts, alerts) };
        var command = SwpcPolicy.Resume(view, null, Guid.NewGuid(), now)!;
        Assert.AreEqual(new SwpcHaltChange(null, null), command.Change);
        Assert.AreEqual(3, command.Checkpoints.Count);
        Assert.IsFalse(command.Checkpoints.ContainsKey(SwpcProduct.Alerts));
        foreach (var checkpoint in command.Checkpoints.Values)
            Assert.AreEqual(new SwpcFetchCheckpoint(now, null, false, 0, 0, 0, null), checkpoint);
        Assert.AreSame(alerts, view.Products[SwpcProduct.Alerts]);
    }

    [TestMethod]
    public async Task ProductResumeUnderProviderHaltClearsProductOnlyAndLeavesCheckpoint()
    {
        using var store = new SwpcPolicyTestStoreDouble();
        var now = SwpcTestData.Now;
        var checkpoint = new SwpcFetchCheckpoint(now.AddHours(1), now.AddHours(2), true, 1, 3, 7, now);
        store.View = store.View with
        {
            ProviderHalt = new("rate-limited", now),
            Products = store.View.Products.SetItem(SwpcProduct.Alerts,
            store.View.Products[SwpcProduct.Alerts] with { ProductHalt = new("not-found", now), Checkpoint = checkpoint })
        };
        using var runner = new SwpcAttemptRunner(store, new PolicyStubClient(), new ManualTimeProvider(now), SwpcJitter.None);
        var result = await runner.ResumeAsync(SwpcProduct.Alerts, Guid.NewGuid()).ConfigureAwait(false);
        Assert.AreEqual(0, store.Halts.Single().Checkpoints.Count);
        Assert.IsNull(result.View.Products[SwpcProduct.Alerts].ProductHalt);
        Assert.IsNotNull(result.View.ProviderHalt);
        Assert.AreSame(checkpoint, result.View.Products[SwpcProduct.Alerts].Checkpoint);
        var next = await runner.ResumeAsync(SwpcProduct.Alerts, Guid.NewGuid()).ConfigureAwait(false);
        Assert.IsTrue(next.NotHalted, "The addressed product scope has no halt even while the provider is halted.");
        Assert.AreEqual(1, store.Halts.Count);
    }

    [TestMethod]
    public void NotHaltedAddressesOnlyRequestedScope()
    {
        var view = SwpcPolicyTestStoreDouble.EmptyView();
        var now = SwpcTestData.Now;
        Assert.IsNull(SwpcPolicy.Resume(view, null, Guid.NewGuid(), now));
        Assert.IsNull(SwpcPolicy.Resume(view, SwpcProduct.Alerts, Guid.NewGuid(), now));
        view = view with { Products = view.Products.SetItem(SwpcProduct.Alerts, view.Products[SwpcProduct.Alerts] with { ProductHalt = new("not-found", now) }) };
        Assert.IsNull(SwpcPolicy.Resume(view, null, Guid.NewGuid(), now));
    }
}
