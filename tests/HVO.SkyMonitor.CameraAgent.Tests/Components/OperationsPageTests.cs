using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class OperationsPageTests
{
    [TestMethod]
    public void LoadingThenEmpty_RendersExplicitStates()
    {
        using var context = new BunitContext();
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentOperationsView>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Configure(context);
        service.OperationsHandler = token => new ValueTask<OperatorUiResult<CameraAgentOperationsView>>(
            pending.Task.WaitAsync(token));

        var cut = context.Render<OperationsPage>();
        StringAssert.Contains(cut.Markup, "Loading current operations", StringComparison.Ordinal);

        pending.SetResult(OperatorUiResult<CameraAgentOperationsView>.Success(OperatorUiTestData.Operations(samples: 0)));
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "No capture activity yet", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FreshDisconnectedAndPressure_RenderTextualStates()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(
                OperatorUiTestData.Operations(heartbeat: "Unavailable", lanePressure: 2, storagePressure: true)));

        var cut = context.Render<OperationsPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "LogicHost connectivity is unavailable", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Storage or lane pressure detected", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Critical pressure", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Fresh", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void InitialFailure_RendersSanitizedError()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unavailable, "Current operations data is unavailable."));

        var cut = context.Render<OperationsPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Operations unavailable", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("Exception", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void PauseConfirmation_PreventsDuplicateSubmitAndAnnouncesReceipt()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        var calls = 0;
        bool? paused = null;
        long? version = null;
        service.CaptureHandler = (requestedPause, expectedVersion, key, _) =>
        {
            calls++;
            paused = requestedPause;
            version = expectedVersion;
            Assert.IsTrue(key.StartsWith("ui-", StringComparison.Ordinal));
            return Task.FromResult(OperatorUiResult<OperatorCommandReceipt>.Success(new(
                "Pause capture", "Applied", "Paused", 8, OperatorUiTestData.Now)));
        };

        var cut = context.Render<OperationsPage>();
        cut.Find("#capture-action").Click();
        cut.WaitForAssertion(() => Assert.AreEqual("DIALOG", cut.Find(".confirmation").TagName));
        cut.Find(".confirmation-actions .btn-primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, calls);
            Assert.AreEqual(true, paused);
            Assert.AreEqual(7, version);
            Assert.AreEqual("status", cut.Find(".receipt--success").GetAttribute("role"));
            StringAssert.Contains(cut.Markup, "Current state: Paused", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void OutboxAbandon_UsesControlledReasonAndAlertOnConflict()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var item = new OperatorOutboxItem(
            "Artifact", "Quarantined", "raw-ingress", "Preview", 2, 100, OperatorUiTestData.Now,
            "invalid-source", "replay-token", "abandon-token");
        var service = Configure(context);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(OperatorUiTestData.Operations(artifactQuarantine: [item])));
        string? reason = null;
        service.OutboxHandler = (_, action, token, selectedReason, _, _) =>
        {
            Assert.AreEqual(OutboxOperationAction.Abandon, action);
            Assert.AreEqual("abandon-token", token);
            reason = selectedReason;
            return ValueTask.FromResult(OperatorUiResult<OperatorCommandReceipt>.Failure(
                OperatorUiResultKind.Conflict, "The outbox item changed. Refresh and review the command again."));
        };

        var cut = context.Render<OperationsPage>();
        cut.Find("button[id$='-abandon']").Click();
        cut.Find("select").Change("irrecoverable-evidence");
        cut.Find(".confirmation-actions .btn-danger").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("irrecoverable-evidence", reason);
            Assert.AreEqual("alert", cut.Find(".dialog-error").GetAttribute("role"));
            Assert.IsNotNull(cut.Find("dialog"));
        });
    }

    [TestMethod]
    public void FailedRefresh_RetainsLastValidDataAndMarksItStale()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        var reads = 0;
        service.OperationsHandler = _ => ++reads == 1
            ? ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Success(OperatorUiTestData.Operations()))
            : ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Failure(
                OperatorUiResultKind.Unavailable, "Current operations data is unavailable."));

        var cut = context.Render<OperationsPage>();
        cut.Find("#capture-action").Click();
        cut.Find(".confirmation-actions .btn-primary").Click();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Showing last valid data", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Raw ingress", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, ">Stale<", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public async Task Disposal_CancelsPendingReadAndAwaitsPollingCleanupAsync()
    {
        using var context = new BunitContext();
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Configure(context);
        service.OperationsHandler = async token =>
        {
            using var registration = token.Register(() => cancellationObserved.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return OperatorUiResult<CameraAgentOperationsView>.Success(OperatorUiTestData.Operations());
        };

        var cut = context.Render<OperationsPage>();
        await cut.Instance.DisposeAsync().ConfigureAwait(false);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Disposal_CancelsAndAwaitsPendingCommandAsync()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Configure(context);
        service.CaptureHandler = async (_, _, _, token) =>
        {
            using var registration = token.Register(() => cancellationObserved.TrySetResult());
            commandStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return OperatorUiResult<OperatorCommandReceipt>.Success(new(
                "Pause capture", "Applied", "Paused", 8, OperatorUiTestData.Now));
        };
        var cut = context.Render<OperationsPage>();
        cut.WaitForElement("#capture-action");
        await cut.Find("#capture-action").ClickAsync().ConfigureAwait(false);
        var commandTask = cut.Find(".confirmation-actions .btn-primary").TriggerEventAsync("onclick", EventArgs.Empty);
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        await cut.Instance.DisposeAsync().ConfigureAwait(false);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await commandTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    [TestMethod]
    public void StaleSection_MarksOverallStateStale()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var current = OperatorUiTestData.Operations();
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(current with
            {
                Summary = current.Summary with
                {
                    RawIngress = current.Summary.RawIngress with { Freshness = "stale" }
                }
            }));

        var cut = context.Render<OperationsPage>();

        cut.WaitForAssertion(() => Assert.AreEqual("Stale", cut.Find(".heading-status .state-chip").TextContent.Trim()));
    }

    [TestMethod]
    public void AmbiguousFailure_RetryReusesRandomIdempotencyKeyUntilSuccess()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        var keys = new List<string>();
        service.CaptureHandler = (_, _, key, _) =>
        {
            keys.Add(key);
            return Task.FromResult(keys.Count == 1
                ? OperatorUiResult<OperatorCommandReceipt>.Failure(
                    OperatorUiResultKind.Unavailable, "The capture command could not be completed.")
                : OperatorUiResult<OperatorCommandReceipt>.Success(new(
                    "Pause capture", "Duplicate receipt", "Paused", 8, OperatorUiTestData.Now)));
        };

        var cut = context.Render<OperationsPage>();
        cut.Find("#capture-action").Click();
        cut.Find(".confirmation-actions .btn-primary").Click();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".dialog-error").TextContent, "could not be completed", StringComparison.Ordinal));
        cut.Find(".confirmation-actions .btn-primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(2, keys);
            Assert.AreEqual(keys[0], keys[1]);
            Assert.IsTrue(keys[0].StartsWith("ui-", StringComparison.Ordinal));
            Assert.AreEqual(67, keys[0].Length);
            Assert.IsEmpty(cut.FindAll("dialog"));
        });
    }

    [TestMethod]
    public async Task CommandAndRefreshRequests_AreSerializedAndCoalescedAsync()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        var active = 0;
        var maximumActive = 0;
        var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.CaptureHandler = async (_, _, _, token) =>
        {
            var nowActive = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximumActive, nowActive);
            commandStarted.SetResult();
            await releaseCommand.Task.WaitAsync(token).ConfigureAwait(false);
            Interlocked.Decrement(ref active);
            return OperatorUiResult<OperatorCommandReceipt>.Success(new(
                "Pause capture", "Applied", "Paused", 8, OperatorUiTestData.Now));
        };
        service.OperationsHandler = async token =>
        {
            var nowActive = Interlocked.Increment(ref active);
            InterlockedExtensions.Max(ref maximumActive, nowActive);
            await Task.Delay(10, token).ConfigureAwait(false);
            Interlocked.Decrement(ref active);
            return OperatorUiResult<CameraAgentOperationsView>.Success(OperatorUiTestData.Operations());
        };

        var cut = context.Render<OperationsPage>();
        cut.WaitForElement("#capture-action");
        await cut.Find("#capture-action").ClickAsync().ConfigureAwait(false);
        var commandTask = cut.Find(".confirmation-actions .btn-primary").TriggerEventAsync("onclick", EventArgs.Empty);
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var refreshTask1 = cut.Find(".refresh-link").TriggerEventAsync("onclick", EventArgs.Empty);
        var refreshTask2 = cut.Find(".refresh-link").TriggerEventAsync("onclick", EventArgs.Empty);
        releaseCommand.SetResult();
        await Task.WhenAll(commandTask, refreshTask1, refreshTask2).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        Assert.AreEqual(1, maximumActive);
    }

    [TestMethod]
    public void NativeDialog_CancelInvokesCloseAndRemovesModal()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        Configure(context);
        var cut = context.Render<OperationsPage>();

        cut.Find("#capture-action").Click();
        cut.WaitForAssertion(() => Assert.IsTrue(context.JSInterop.Invocations.Any(static invocation => invocation.Identifier == "showModal")));
        cut.Find("dialog").TriggerEvent("oncancel", EventArgs.Empty);

        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll("dialog"));
            Assert.IsTrue(context.JSInterop.Invocations.Any(static invocation => invocation.Identifier == "close"));
            Assert.IsEmpty(cut.FindAll("main"));
        });
    }

    private static TestOperatorUiService Configure(BunitContext context)
    {
        var service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(OperatorUiTestData.Now));
        return service;
    }
}

internal static class InterlockedExtensions
{
    internal static void Max(ref int location, int value)
    {
        var current = Volatile.Read(ref location);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current)
            {
                return;
            }
            current = observed;
        }
    }
}
