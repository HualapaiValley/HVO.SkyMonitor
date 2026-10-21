using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;

[TestClass]
[TestCategory("Unit")]
public sealed class SpaceWeatherRefreshJobTests
{
    [TestMethod]
    [DataRow(200, LocalAutomationRunOutcome.Succeeded)]
    [DataRow(503, LocalAutomationRunOutcome.Failed)]
    [DataRow(429, LocalAutomationRunOutcome.Failed)]
    [DataRow(403, LocalAutomationRunOutcome.Failed)]
    [DataRow(404, LocalAutomationRunOutcome.Failed)]
    public async Task ProviderOutcomesAndRetentionAreComposed(int status, LocalAutomationRunOutcome expected)
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        host.Client.Status = status;
        var result = await host.RunAsync().ConfigureAwait(false);
        Assert.AreEqual(expected, result.Outcome, result.Detail);
        Assert.AreEqual("retention", host.Store.Calls[0]);
        Assert.AreEqual(1, host.Store.Calls.Count(static call => call == "retention"));
        Assert.IsLessThanOrEqualTo(512, result.Detail.Length);
        var tokens = result.Detail.Split(": ")[1].Split(' ');
        CollectionAssert.AreEqual(tokens.Order(StringComparer.Ordinal).ToArray(), tokens);
    }

    [TestMethod]
    public async Task DisabledAndUnavailableRunsSkipWithoutAttempts()
    {
        using var disabled = new SpaceWeatherTestHost(false); await disabled.InitializeAsync().ConfigureAwait(false);
        Assert.AreEqual("space-weather.disabled", (await disabled.RunAsync().ConfigureAwait(false)).Detail);
        Assert.AreEqual(0, disabled.Client.Calls);
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        host.Availability.RecordFailure(new IOException("secret")); host.Sqlite.Dispose();
        Assert.AreEqual("space-weather.store-unavailable", (await host.RunAsync().ConfigureAwait(false)).Detail);
        Assert.AreEqual(0, host.Client.Calls);
    }

    [TestMethod]
    public async Task AllNotDueAndAllHaltedSkipButManualSpacingIsNormalSuccess()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        await host.RunAsync().ConfigureAwait(false);
        Assert.StartsWith("space-weather.nothing-due", (await host.RunAsync().ConfigureAwait(false)).Detail);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, (await host.RunAsync(SwpcAttemptTrigger.Manual).ConfigureAwait(false)).Outcome);
        host.Clock.Advance(TimeSpan.FromHours(1)); await host.HaltAsync().ConfigureAwait(false);
        Assert.StartsWith("space-weather.halted", (await host.RunAsync().ConfigureAwait(false)).Detail);
    }

    [TestMethod]
    public async Task TheThirtySecondBudgetStartsNoFurtherProducts()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        host.Client.AdvancePerFetch = TimeSpan.FromSeconds(30);
        var result = await host.RunAsync().ConfigureAwait(false);
        Assert.AreEqual(1, host.Client.Calls);
        StringAssert.Contains(result.Detail, "scales=budget", StringComparison.Ordinal);
        StringAssert.Contains(result.Detail, "3-day-forecast=budget", StringComparison.Ordinal);
        Assert.AreEqual(1, host.Logs.Count(static entry => entry.Id == 7827));
    }

    [TestMethod]
    public async Task RetentionFailureStopsBeforeAnyProductAndDisclosesOnlyType()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        host.Store.RetentionFailure = new IOException("retention-secret");
        var result = await host.RunAsync().ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, result.Outcome);
        Assert.AreEqual("space-weather.retention-failed", result.Detail);
        Assert.AreEqual(0, host.Client.Calls);
        Assert.IsFalse(host.Logs.Any(static log => log.Text.Contains("retention-secret", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CancellationPropagatesAndDoesNotBecomeFailed()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource(); await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => host.RunAsync(ct: cancellation.Token).AsTask()).ConfigureAwait(false);
        Assert.AreEqual(0, host.Client.Calls);
        Assert.IsFalse(host.Logs.Any(static log => log.Id == 7826));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartupIncompleteObservationRunsOnceBeforeEitherFirstRunnerPath(bool resumeFirst)
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        var view = await host.ViewAsync().ConfigureAwait(false);
        await host.Store.ChangeHaltAsync(new(Guid.NewGuid(), view.Revision, host.Clock.GetUtcNow(), new(null, new("forbidden", host.Clock.GetUtcNow())),
            System.Collections.Immutable.ImmutableDictionary<SwpcProduct, SwpcFetchCheckpoint>.Empty), default).ConfigureAwait(false);
        view = await host.ViewAsync().ConfigureAwait(false);
        var admission = SwpcPolicy.Admit(view with { ProviderHalt = null }, SwpcProduct.Alerts, SwpcAttemptTrigger.Manual, "interrupted", Guid.NewGuid(), host.Clock.GetUtcNow());
        await host.Store.BeginAttemptAsync(admission.Start!, default).ConfigureAwait(false);
        if (resumeFirst) await host.Operations.ResumeAsync("provider", null, host.Clock.GetUtcNow(), "startup", default).ConfigureAwait(false);
        else await host.RunAsync().ConfigureAwait(false);
        await host.RunAsync().ConfigureAwait(false);
        Assert.AreEqual(1, host.Logs.Count(static log => log.Id == 7829));
        Assert.AreEqual(0, (await host.Store.ReadHealthAsync(host.Clock.GetUtcNow(), default).ConfigureAwait(false)).IncompleteAttempts);
    }

    [TestMethod]
    [DataRow(200)]
    [DataRow(304)]
    public async Task UnchangedAndNotModifiedRetrievalsAreSuccessfulRuns(int status)
    {
        using var host = new SpaceWeatherTestHost(products: [SwpcProduct.Alerts]); await host.InitializeAsync().ConfigureAwait(false);
        await host.RunAsync().ConfigureAwait(false); host.Clock.Advance(TimeSpan.FromSeconds(120)); host.Client.Status = status;
        var result = await host.RunAsync().ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, result.Outcome);
        StringAssert.Contains(result.Detail, status == 304 ? "alerts=not-modified" : "alerts=unchanged", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AStaleSnapshotKeepsLkgAndStillMapsToSuccessfulComposition()
    {
        using var host = new SpaceWeatherTestHost(products: [SwpcProduct.Alerts]); await host.InitializeAsync().ConfigureAwait(false);
        host.Client.Body = _ => System.Text.Encoding.UTF8.GetBytes(SwpcStoreTests.Message());
        await host.RunAsync().ConfigureAwait(false); var first = (await host.ViewAsync().ConfigureAwait(false)).Products[SwpcProduct.Alerts];
        host.Client.Body = SpaceWeatherTestHost.TestClient.Capture; host.Clock.Advance(TimeSpan.FromSeconds(120));
        var result = await host.RunAsync().ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, result.Outcome); StringAssert.Contains(result.Detail, "alerts=stale-snapshot", StringComparison.Ordinal);
        Assert.AreEqual(first.LastKnownGood!.Sha256, (await host.ViewAsync().ConfigureAwait(false)).Products[SwpcProduct.Alerts].LastKnownGood!.Sha256);
    }

    [TestMethod]
    public async Task AStorageExceptionAfterAdmissionFailsWithOnlyItsTypeAndSettlesTheAttempt()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        host.Client.Fetch = (_, _) => throw new SwpcStorageException("private-failure-sentinel");
        var result = await host.RunAsync().ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, result.Outcome); Assert.StartsWith("space-weather.storage-failed", result.Detail);
        Assert.IsFalse(host.Logs.Any(static log => log.Text.Contains("private-failure-sentinel", StringComparison.Ordinal)));
        var journal = await host.Sqlite.ReadDiagnosticsAsync(new(host.Clock.GetUtcNow(), null, null, 10), default).ConfigureAwait(false);
        Assert.IsNotNull(journal.Attempts.Single().EndedUtc);
    }

    [TestMethod]
    public async Task CancellationDuringFetchPropagatesAfterDurableInterruption()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Client.Fetch = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); throw new AssertFailedException("Cancelled fetch returned."); };
        using var cancellation = new CancellationTokenSource(); var run = host.RunAsync(ct: cancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => run).ConfigureAwait(false);
        var journal = await host.Sqlite.ReadDiagnosticsAsync(new(host.Clock.GetUtcNow(), null, null, 10), default).ConfigureAwait(false);
        Assert.AreEqual("interrupted", journal.Attempts.Single().ReasonCode);
        Assert.IsFalse(host.Logs.Any(static entry => entry.Id == 7826));
    }

    [TestMethod]
    public void DetailHasAnOrdinalOrderAndHardBound()
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["scales"] = new('a', 600), ["alerts"] = "updated" };
        var result = SpaceWeatherRefreshJob.Result(LocalAutomationRunOutcome.Succeeded, "space-weather.succeeded", values);
        Assert.AreEqual(512, result.Detail.Length);
        Assert.StartsWith("space-weather.succeeded: alerts=updated scales=", result.Detail);
    }

    [TestMethod]
    public async Task RejectedBodiesFailAndConfiguredProductSelectionIsRespected()
    {
        using var host = new SpaceWeatherTestHost(products: [SwpcProduct.Scales]); await host.InitializeAsync().ConfigureAwait(false);
        host.Client.Body = _ => "{}"u8.ToArray();
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, (await host.RunAsync().ConfigureAwait(false)).Outcome);
        Assert.AreEqual(1, host.Client.Calls);
        Assert.IsNull((await host.ViewAsync().ConfigureAwait(false)).Products[SwpcProduct.Alerts].LastAttemptUtc);
    }
}
