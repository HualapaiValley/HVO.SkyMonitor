using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
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
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "No capture activity yet", StringComparison.Ordinal);
            Assert.HasCount(1, cut.FindAll(".operating-deck"));
            Assert.HasCount(1, cut.FindAll("#attention-heading"));
            Assert.HasCount(4, cut.FindAll(".configuration-links a"));
            Assert.HasCount(1, cut.FindAll(".grid-heading"));
            Assert.HasCount(2, cut.FindAll(".secondary-details > summary"));
            Assert.AreEqual("/", cut.Find(".operating-deck a[href='/']").GetAttribute("href"));
            StringAssert.Contains(cut.Find(".configuration-links").TextContent, "profile r2", StringComparison.Ordinal);
            Assert.HasCount(2, cut.FindAll(".change-list li"));
            Assert.AreEqual("Profile r2", cut.Find(".change-list li strong").TextContent);
            StringAssert.Contains(cut.Find(".recent-changes").TextContent, "Recent profile revisions", StringComparison.Ordinal);
            Assert.AreEqual("/operations/schedule", cut.Find(".configuration-links a[href='/operations/schedule']").GetAttribute("href"));
        });
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
            StringAssert.Contains(cut.Markup, "Inspect storage", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Inspect status", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Critical pressure", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("_view.Summary", StringComparison.Ordinal));
            StringAssert.Contains(cut.Markup, "Fresh", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void StandaloneMode_RendersNeutralCurrentStateWithoutDisconnectedWarning()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(
                OperatorUiTestData.Operations(
                    heartbeat: "Disabled",
                    centralIntegration: "Disabled")));

        var cut = context.Render<OperationsPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Raw ingress", StringComparison.OrdinalIgnoreCase);
            StringAssert.Contains(cut.Markup, ">Current<", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("connectivity is unavailable", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(cut.Markup.Contains(">Disconnected<", StringComparison.Ordinal));
            StringAssert.Contains(cut.Find(".operating-deck").TextContent, "Disabled", StringComparison.Ordinal);
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
            StringAssert.Contains(cut.Markup, "Some operating facts are stale", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".operating-deck").TextContent, "Last observed state", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Raw ingress", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, ">Stale<", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Disposal_CancelsPendingReadAndAwaitsPollingCleanupAsync()
    {
        using var context = new BunitContext();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Configure(context);
        service.OperationsHandler = async token =>
        {
            using var registration = token.Register(() => cancellationObserved.TrySetResult());
            readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return OperatorUiResult<CameraAgentOperationsView>.Success(OperatorUiTestData.Operations());
        };

        var cut = context.Render<OperationsPage>();
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await cut.Instance.DisposeAsync().ConfigureAwait(false);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    [TestMethod]
    [DoNotParallelize]
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
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        await cut.Instance.DisposeAsync().ConfigureAwait(false);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await commandTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
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

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Stale", cut.Find(".heading-status .state-chip").TextContent.Trim());
            StringAssert.Contains(cut.Markup, "Some operating facts are stale", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void UnknownFacts_DoNotAnnounceAllClear()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var current = OperatorUiTestData.Operations();
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(current with
            {
                Summary = current.Summary with
                {
                    Storage = current.Summary.Storage with { Freshness = "unknown" }
                }
            }));
        var cut = context.Render<OperationsPage>();
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Some operating facts have not been observed", StringComparison.Ordinal);
            Assert.AreEqual("Needs review", cut.Find(".heading-status .state-chip").TextContent.Trim());
            Assert.IsFalse(cut.Markup.Contains("No pressure, retries, or quarantined delivery items reported", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void UnknownHeartbeat_DoesNotInventAConnectionOutage()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var current = OperatorUiTestData.Operations(heartbeat: "Unavailable");
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(current with
            {
                Summary = current.Summary with
                {
                    Heartbeat = current.Summary.Heartbeat with { Freshness = "unknown" }
                }
            }));
        var cut = context.Render<OperationsPage>();
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Some operating facts have not been observed", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("LogicHost connectivity is unavailable", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void UnavailableSchedule_DoesNotInventActiveRevisionOrRecentChanges()
    {
        using var context = new BunitContext();
        Configure(context);
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(new UnavailableScheduleUiService());
        var cut = context.Render<OperationsPage>();
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find(".configuration-links").TextContent, "revision unavailable", StringComparison.OrdinalIgnoreCase);
            StringAssert.Contains(cut.Find(".recent-changes").TextContent, "Recent profile revisions are unavailable", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll(".change-list li"));
        });
    }

    [TestMethod]
    public void LatestLiveRun_LinksToRecordedExecution()
    {
        using var context = new BunitContext();
        Configure(context);
        var runId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var run = ProcessingExecutionPagesTests.Execution(runId, ProcessingGraphExecutionClass.Live,
            ProcessingGraphExecutionStatus.Completed);
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new ProcessingExecutionPagesTests.GraphUiService
        {
            Executions = new CameraAgentProcessingExecutionsView(OperatorUiTestData.Now, 1,
                [CameraAgentProcessingExecutionProjection.Summarize(run)], [])
        });
        var cut = context.Render<OperationsPage>();
        cut.WaitForAssertion(() => Assert.AreEqual($"/operations/pipeline/executions/{runId}",
            cut.Find(".operating-deck a[href*='/pipeline/executions/']").GetAttribute("href")));
    }

    [TestMethod]
    public void InitializingCapture_DoesNotInventAnInFlightCommand()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var current = OperatorUiTestData.Operations();
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(current with
            {
                Summary = current.Summary with
                {
                    CaptureControl = current.Summary.CaptureControl with
                    {
                        Value = current.Summary.CaptureControl.Value with { State = "Initializing" }
                    }
                }
            }));
        var cut = context.Render<OperationsPage>();
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "pause and resume are unavailable", StringComparison.Ordinal);
            Assert.IsFalse(cut.Markup.Contains("command is settling", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void EnvironmentalDeliveryAttention_LinksToEnvironmentalQuarantine()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var current = OperatorUiTestData.Operations();
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(current with
            {
                Summary = current.Summary with
                {
                    EnvironmentalDelivery = current.Summary.EnvironmentalDelivery with
                    {
                        Value = current.Summary.EnvironmentalDelivery.Value with { RetryCount = 1 }
                    }
                }
            }));
        var cut = context.Render<OperationsPage>();
        cut.WaitForAssertion(() => Assert.AreEqual("/operations/data",
            cut.Find(".attention-list a[href='/operations/data']").GetAttribute("href")));
    }

    [TestMethod]
    public void QuarantinedArtifact_LinksToHeldItems()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(OperatorUiTestData.Operations(
                artifactQuarantine: [new OperatorOutboxItem("Artifact", "Quarantined", "raw-ingress", "Preview",
                    1, 100, OperatorUiTestData.Now, "invalid-source", "replay-token", "abandon-token")])));
        var cut = context.Render<OperationsPage>();
        cut.WaitForAssertion(() => Assert.AreEqual("/operations/quarantine",
            cut.Find(".attention-list a[href='/operations/quarantine']").GetAttribute("href")));
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
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(
            new SchedulePageTests.ScheduleUiService(SchedulePageTests.State()));
        context.Services.AddSingleton<ICameraAgentCalibrationUiService>(new OverviewCalibrationUiService());
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new ProcessingExecutionPagesTests.GraphUiService
        {
            Executions = new CameraAgentProcessingExecutionsView(OperatorUiTestData.Now, 1, [], [])
        });
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(OperatorUiTestData.Now));
        return service;
    }

    private sealed class UnavailableScheduleUiService : ICameraAgentScheduleUiService
    {
        public ValueTask<OperatorUiResult<HVO.SkyMonitor.CameraAgent.Common.Scheduling.CaptureScheduleOperatorState>> GetAsync(
            CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<HVO.SkyMonitor.CameraAgent.Common.Scheduling.CaptureScheduleOperatorState>
                .Failure(OperatorUiResultKind.Unavailable, "Schedule unavailable."));

        public ValueTask<OperatorUiResult<HVO.SkyMonitor.CameraAgent.Common.Scheduling.CaptureSchedulePreview>> PreviewAsync(
            string profileJson, string basisRevisionId, int dayCount, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<HVO.SkyMonitor.CameraAgent.Common.Scheduling.CaptureScheduleStoreSnapshot>> StageAsync(
            string profileJson, string basisRevisionId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<HVO.SkyMonitor.CameraAgent.Common.Scheduling.CaptureScheduleStoreSnapshot>> ActivateAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<HVO.SkyMonitor.CameraAgent.Common.Scheduling.CaptureScheduleStoreSnapshot>> RollbackAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<HVO.SkyMonitor.CameraAgent.Common.Scheduling.CaptureScheduleStoreSnapshot>> AddOverrideAsync(
            HVO.SkyMonitor.CameraAgent.Common.Scheduling.CaptureScheduleOverride scheduleOverride, long expectedVersion,
            string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<HVO.SkyMonitor.CameraAgent.Common.Scheduling.CaptureScheduleStoreSnapshot>> ClearOverrideAsync(
            string overrideId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class OverviewCalibrationUiService : ICameraAgentCalibrationUiService
    {
        public ValueTask<OperatorUiResult<CalibrationUiStatus>> GetStatusAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CalibrationUiStatus>.Success(new(
                1, null, null, 0, 0, null, null, null, null, null)));

        public ValueTask<OperatorUiResult<CalibrationUiBundlePage>> GetBundlesAsync(int pageSize, string? cursor,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CalibrationUiBundleDetail>> GetBundleAsync(string bundleId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CalibrationUiAcquisition>> AcquireAsync(CalibrationUiAcquisitionRequest request,
            long expectedVersion, string idempotencyKey, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CalibrationUiAcquisition>> CancelAsync(string jobId, long expectedVersion,
            string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CalibrationUiStatus>> ActivateAsync(string bundleId, long expectedVersion,
            string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CalibrationUiStatus>> RollbackAsync(string bundleId, long expectedVersion,
            string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();
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
