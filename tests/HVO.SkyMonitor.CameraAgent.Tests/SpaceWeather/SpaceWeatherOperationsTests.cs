using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;

[TestClass]
[TestCategory("Unit")]
public sealed class SpaceWeatherOperationsTests
{
    [TestMethod]
    public void CommandIdentitiesHaveTheD1KnownAnswerVersionVariantAndKindSeparation()
    {
        var id = SpaceWeatherOperations.CommandId("resume", "test-key");
        Assert.AreEqual("79be1650-773e-8fe1-bd31-34bb7d225b79", id.ToString("D"));
        Assert.AreEqual(id, SpaceWeatherOperations.CommandId("resume", "test-key"));
        Assert.AreNotEqual(id, SpaceWeatherOperations.CommandId("acknowledge", "test-key"));
        Assert.AreNotEqual(Guid.Empty, id);
        Assert.AreEqual('8', id.ToString("D")[14]);
        Assert.IsTrue(id.ToString("D")[19] is '8' or '9' or 'a' or 'b');
    }

    [TestMethod]
    public async Task StatusIsRedactedAndRefreshQueuesOnceThroughRealAutomationReplay()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        var missing = await host.Operations.RefreshAsync("missing", "owner", default).ConfigureAwait(false);
        Assert.AreEqual("space-weather.definition-missing", missing.ReasonCode);
        await host.SaveAsync().ConfigureAwait(false);
        var queued = await host.Operations.RefreshAsync("refresh", "owner", default).ConfigureAwait(false);
        var replay = await host.Operations.RefreshAsync("refresh", "owner", default).ConfigureAwait(false);
        Assert.AreEqual(SpaceWeatherOperations.Disposition.Accepted, queued.Disposition);
        Assert.AreEqual(SpaceWeatherOperations.Disposition.Replayed, replay.Disposition);
        Assert.IsTrue(replay.Status.DefinitionExists); Assert.IsTrue(replay.Status.DefinitionEnabled);
        Assert.AreEqual(4, replay.Status.Products.Count); Assert.AreEqual(SpaceWeatherOperations.Credit, replay.Status.Credit);
        Assert.AreEqual(0, host.Client.Calls);
        Assert.AreEqual(1, (await host.Automation.GetStateAsync(default).ConfigureAwait(false)).Runs.Count);
        using var runner = host.CreateRunner();
        await runner.SweepAsync(default).ConfigureAwait(false); await host.Worker.WhenIdleAsync().ConfigureAwait(false);
        Assert.AreEqual(4, host.Client.Calls);
        Assert.IsTrue((await host.Store.ReadDiagnosticsAsync(new(host.Clock.GetUtcNow(), null, null, 10), default).ConfigureAwait(false))
            .Attempts.All(static attempt => attempt.Trigger == SwpcAttemptTrigger.Manual));
    }

    [TestMethod]
    public async Task ResumeUsesTheExactStatusHaltInstantAndNeverReappliesARetainedKey()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        var since = await host.HaltAsync().ConfigureAwait(false);
        var status = await host.Operations.GetStatusAsync(default).ConfigureAwait(false);
        Assert.AreEqual(since.ToString("O", CultureInfo.InvariantCulture), status.ProviderHalt!.SinceUtc);
        var echoed = DateTimeOffset.Parse(status.ProviderHalt.SinceUtc, CultureInfo.InvariantCulture);
        var result = await host.Operations.ResumeAsync("provider", null, echoed, "resume", default).ConfigureAwait(false);
        Assert.AreEqual("resumed", result.Outcome); Assert.IsNull(result.Status.ProviderHalt);
        Assert.AreEqual("not-halted", (await host.Operations.ResumeAsync("provider", null, echoed, "resume", default).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(1L, host.Count("swpc_receipts"));
        host.Clock.Advance(TimeSpan.FromSeconds(60)); var later = await host.HaltAsync().ConfigureAwait(false);
        Assert.AreEqual("space-weather.resume-halt-changed", (await host.Operations.ResumeAsync("provider", null, echoed, "resume", default).ConfigureAwait(false)).ReasonCode);
        Assert.AreEqual("space-weather.idempotency-key-reused", (await host.Operations.ResumeAsync("provider", null, later, "resume", default).ConfigureAwait(false)).ReasonCode);
        Assert.AreEqual(1L, host.Count("swpc_receipts")); Assert.IsNotNull((await host.ViewAsync().ConfigureAwait(false)).ProviderHalt);
    }

    [TestMethod]
    public async Task TheReceiptGuaranteeEndsAfterThirtyFiveDaysButOldHaltIdentityStillProtectsLaterHalts()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        var old = await host.HaltAsync().ConfigureAwait(false);
        await host.Operations.ResumeAsync("provider", null, old, "bounded", default).ConfigureAwait(false);
        host.Clock.Advance(TimeSpan.FromDays(36)); var current = await host.HaltAsync().ConfigureAwait(false);
        Assert.AreEqual(0L, host.Count("swpc_receipts"));
        Assert.AreEqual("space-weather.resume-halt-changed", (await host.Operations.ResumeAsync("provider", null, old, "bounded", default).ConfigureAwait(false)).ReasonCode);
        Assert.AreEqual("resumed", (await host.Operations.ResumeAsync("provider", null, current, "bounded", default).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(1L, host.Count("swpc_receipts"));
    }

    [TestMethod]
    public async Task AcknowledgementReplaysAtTheSameTimeAndLaterReuseCannotChangeIt()
    {
        using var host = new SpaceWeatherTestHost(products: [SwpcProduct.Alerts]); await host.InitializeAsync().ConfigureAwait(false);
        host.Client.Body = _ => System.Text.Encoding.UTF8.GetBytes(SwpcStoreTests.Message()); await host.RunAsync().ConfigureAwait(false);
        var group = (await host.ViewAsync().ConfigureAwait(false)).Notices.Groups.Single().Identity;
        Assert.AreEqual("acknowledged", (await host.Operations.AcknowledgeAsync(group, "owner", "ack", default).ConfigureAwait(false)).Outcome);
        Assert.AreEqual("replayed", (await host.Operations.AcknowledgeAsync(group, "owner", "ack", default).ConfigureAwait(false)).Outcome);
        var original = (await host.ViewAsync().ConfigureAwait(false)).Notices.Acknowledgements.Single();
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.AreEqual("space-weather.idempotency-key-reused", (await host.Operations.AcknowledgeAsync(group, "owner", "ack", default).ConfigureAwait(false)).ReasonCode);
        Assert.AreEqual(original, (await host.ViewAsync().ConfigureAwait(false)).Notices.Acknowledgements.Single());
        Assert.AreEqual(1L, host.Count("swpc_acks")); Assert.AreEqual(1L, host.Count("swpc_receipts"));
        Assert.AreEqual(SpaceWeatherOperations.Disposition.NotFound,
            (await host.Operations.AcknowledgeAsync("ALTZZZ/999", "owner", "unknown", default).ConfigureAwait(false)).Disposition);
    }

    [TestMethod]
    public async Task ARevisionConflictWritesNoReceiptAndTheSameKeyCanThenSucceed()
    {
        using var host = new SpaceWeatherTestHost(products: [SwpcProduct.Alerts]); await host.InitializeAsync().ConfigureAwait(false);
        host.Client.Body = _ => System.Text.Encoding.UTF8.GetBytes(SwpcStoreTests.Message()); await host.RunAsync().ConfigureAwait(false);
        var group = (await host.ViewAsync().ConfigureAwait(false)).Notices.Groups.Single().Identity;
        host.Store.AckConflictOnce = true;
        Assert.AreEqual("space-weather.revision-conflict", (await host.Operations.AcknowledgeAsync(group, "owner", "retry", default).ConfigureAwait(false)).ReasonCode);
        Assert.AreEqual(0L, host.Count("swpc_receipts"));
        Assert.AreEqual("acknowledged", (await host.Operations.AcknowledgeAsync(group, "owner", "retry", default).ConfigureAwait(false)).Outcome);
        Assert.AreEqual(1L, host.Count("swpc_receipts"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ASaturatedGateReturnsBusyWithFreshStatusAfterFiveSecondsOfManualTime(bool acknowledge)
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Client.Fetch = async (product, ct) =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(ct).ConfigureAwait(false);
            return new(product, host.Clock.GetUtcNow(), host.Clock.GetUtcNow(), HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch.SwpcTransportFailure.None, 503, false);
        };
        var running = host.RunAsync().AsTask(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var pending = acknowledge ? host.Operations.AcknowledgeAsync("ALTZZZ/1", "owner", "busy", default).AsTask()
            : host.Operations.ResumeAsync("provider", null, host.Clock.GetUtcNow(), "busy", default).AsTask();
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        var busy = await pending.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual("space-weather.busy", busy.ReasonCode); Assert.IsTrue(busy.Status.StoreAvailable);
        Assert.AreEqual(host.Clock.GetUtcNow(), busy.Status.ObservedUtc); Assert.AreEqual(0L, host.Count("swpc_receipts"));
        release.TrySetResult(); await running.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ConcurrentJobAndResumeCompleteWithoutDeadlockAndRequestCancellationIsNotBusy()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        var since = await host.HaltAsync().ConfigureAwait(false);
        await Task.WhenAll(host.RunAsync().AsTask(), host.Operations.ResumeAsync("provider", null, since, "parallel", default).AsTask())
            .WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await host.Gate.EnterAsync(default).ConfigureAwait(false);
        try
        {
            using var cancel = new CancellationTokenSource();
            var pending = host.Operations.ResumeAsync("provider", null, since, "cancel", cancel.Token).AsTask();
            await cancel.CancelAsync().ConfigureAwait(false); await Assert.ThrowsAsync<OperationCanceledException>(() => pending).ConfigureAwait(false);
        }
        finally { host.Gate.Exit(); }
    }

    [TestMethod]
    [DataRow(null, "provider", null, "space-weather.resume-invalid-halt-time")]
    [DataRow("2026-10-20T12:00:00+01:00", "provider", null, "space-weather.resume-invalid-halt-time")]
    [DataRow("2026-10-20T12:00:00Z", "provider", "alerts", "space-weather.resume-invalid-scope")]
    [DataRow("2026-10-20T12:00:00Z", "product", "ovation", "space-weather.resume-invalid-scope")]
    public async Task ResumeValidatesTimeAndScope(string? at, string scope, string? product, string reason)
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        var time = at is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(at, CultureInfo.InvariantCulture);
        Assert.AreEqual(reason, (await host.Operations.ResumeAsync(scope, product, time, "invalid", default).ConfigureAwait(false)).ReasonCode);
        Assert.AreEqual(0L, host.Count("swpc_receipts")); Assert.IsTrue(host.Logs.Any(static log => log.Id == 7832));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(51)]
    public async Task NoticePagingRejectsBoundsAndKeepsAnExclusiveOrdinalCursor(int invalid)
    {
        using var host = new SpaceWeatherTestHost(products: [SwpcProduct.Alerts]); await host.InitializeAsync().ConfigureAwait(false);
        await host.RunAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Operations.GetNoticesAsync(null, null, invalid).AsTask()).ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Operations.GetNoticesAsync("invalid", null).AsTask()).ConfigureAwait(false);
        var first = await host.Operations.GetNoticesAsync(null, null, 1).ConfigureAwait(false);
        Assert.IsNotNull(first.NextCursor);
        var second = await host.Operations.GetNoticesAsync(null, first.NextCursor, 1).ConfigureAwait(false);
        Assert.IsTrue(StringComparer.Ordinal.Compare(first.Notices[0].Group, second.Notices[0].Group) < 0);
        Assert.AreEqual(first.Notices[0], (await host.Operations.GetNoticesAsync(null, null, 1).ConfigureAwait(false)).Notices[0]);
    }
}
