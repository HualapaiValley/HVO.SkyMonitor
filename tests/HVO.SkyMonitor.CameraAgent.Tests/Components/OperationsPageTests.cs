using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class OperationsPageTests
{
    private static readonly string[] LoadedFacts = ["Not reported", "Schedule open", "Installed rig", "Available"];

    [TestMethod]
    public void LoadingThenLoaded_RendersPrototypeOverviewFromRecordedSources()
    {
        using var context = new BunitContext();
        var pending = new TaskCompletionSource<OperatorUiResult<CameraAgentOperationsView>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Configure(context);
        service.OperationsHandler = token => new ValueTask<OperatorUiResult<CameraAgentOperationsView>>(
            pending.Task.WaitAsync(token));

        var cut = context.Render<OperationsPage>();
        Assert.AreEqual("true", cut.Find(".ops-state-deck.pending").GetAttribute("aria-busy"));
        StringAssert.Contains(cut.Markup, "Loading current operations", StringComparison.Ordinal);

        pending.SetResult(OperatorUiResult<CameraAgentOperationsView>.Success(OperatorUiTestData.Operations(samples: 0)));
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Operations", cut.Find("h1#operations-heading").TextContent.Trim());
            StringAssert.Contains(cut.Markup, "North Camera / local authority", StringComparison.Ordinal);
            Assert.DoesNotContain("agent-test", cut.Find(".ops-page-heading").TextContent);
            Assert.AreEqual("/", cut.Find("a.button.secondary[href='/']").GetAttribute("href"));

            var deck = cut.Find(".ops-state-deck");
            Assert.AreEqual("Capture loop running", deck.QuerySelector("strong")!.TextContent);
            StringAssert.Contains(deck.TextContent, "No capture activity yet", StringComparison.Ordinal);
            var facts = cut.FindAll(".ops-state-facts dd").Select(static fact => fact.TextContent).ToArray();
            CollectionAssert.AreEqual(LoadedFacts, facts);
            Assert.AreEqual("Processing runs", cut.Find(".ops-state-deck a.button.primary[href='/operations/pipeline/executions']").TextContent);

            Assert.AreEqual("0 open", cut.Find("#attention-heading").ParentElement!.ParentElement!.QuerySelector(":scope > span")!.TextContent);
            StringAssert.Contains(cut.Find(".ops-panel-empty").TextContent, "No open items in the latest reading.", StringComparison.Ordinal);

            Assert.HasCount(4, cut.FindAll(".ops-config-card"));
            AssertCard(cut, "/operations/camera", "Installed rig / r1", "Module Virtual Sky");
            AssertCard(cut, "/operations/schedule", "Schedule r2", "Next transition 00:00 UTC");
            AssertCard(cut, "/operations/pipeline", "Pipeline r2", "1 configured step");
            AssertCard(cut, "/operations/calibration", "No active bundle", "0 published bundles");

            var lanes = cut.FindAll(".ops-lane");
            Assert.HasCount(4, lanes);
            StringAssert.Contains(lanes[0].TextContent, "0 pending", StringComparison.Ordinal);
            var meter = cut.Find(".ops-meter.blue[role='meter']");
            Assert.AreEqual("50", meter.GetAttribute("aria-valuenow"));
            StringAssert.Contains(lanes[3].TextContent, "50% used", StringComparison.Ordinal);

            StringAssert.Contains(cut.Markup, "No capture activity yet", StringComparison.Ordinal);
            Assert.HasCount(2, cut.FindAll(".ops-metric strong").Where(static metric => metric.TextContent == "Not reported").ToArray());

            var changes = cut.FindAll(".ops-audit-list li");
            Assert.HasCount(2, changes);
            Assert.AreEqual("Profile", changes[0].QuerySelector("strong")!.TextContent);
            Assert.AreEqual("Revision r2 saved from an operator draft; active.", changes[0].QuerySelector("span")!.TextContent);
            Assert.AreEqual("Revision r1 saved from an operator draft.", changes[1].QuerySelector("span")!.TextContent);
            Assert.AreEqual("1970-01-01T00:00:00.0000000+00:00", changes[0].QuerySelector("time")!.GetAttribute("datetime"));
            Assert.AreEqual("Jan 1", changes[0].QuerySelector("time")!.TextContent);

            Assert.IsTrue(cut.Find(".toast").HasAttribute("hidden"));
            Assert.IsEmpty(cut.FindAll("dialog"));
        });
    }

    [TestMethod]
    public void CaptureActivity_ReportsModeSetpointAndAverages()
    {
        using var context = new BunitContext();
        Configure(context);

        var cut = context.Render<OperationsPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find(".ops-state-deck").TextContent, "Still mode / setpoint night", StringComparison.Ordinal);
            var metrics = cut.FindAll(".ops-metric");
            StringAssert.Contains(metrics[0].TextContent, "1s", StringComparison.Ordinal);
            StringAssert.Contains(metrics[1].TextContent, "Average of recent captures", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void FreshDisconnectedAndPressure_RaiseAttentionItems()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(
                OperatorUiTestData.Operations(heartbeat: "Unavailable", lanePressure: 2, storagePressure: true)));

        var cut = context.Render<OperationsPage>();

        cut.WaitForAssertion(() =>
        {
            var items = cut.FindAll(".ops-attention-item");
            Assert.HasCount(2, items);
            StringAssert.Contains(items[0].TextContent, "LogicHost connection unavailable", StringComparison.Ordinal);
            Assert.AreEqual("/operations/system", items[0].QuerySelector("a")!.GetAttribute("href"));
            StringAssert.Contains(items[1].TextContent, "Storage or lane pressure", StringComparison.Ordinal);
            StringAssert.Contains(items[1].TextContent, "Under pressure: Raw ingress storage, Standard lane.", StringComparison.Ordinal);
            Assert.AreEqual("/operations/storage", items[1].QuerySelector("a")!.GetAttribute("href"));
            Assert.IsNotNull(cut.Find(".ops-meter.attention[role='meter']"));
            Assert.AreEqual("Unavailable", cut.FindAll(".ops-state-facts dd")[3].TextContent);
            Assert.IsFalse(cut.Markup.Contains("_view.Summary", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void StandaloneMode_RendersDisabledCentralWithoutDisconnectedWarning()
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
            Assert.AreEqual("Disabled", cut.FindAll(".ops-state-facts dd")[3].TextContent);
            var delivery = cut.FindAll(".ops-lane")[2];
            StringAssert.Contains(delivery.TextContent, "Central integration is disabled", StringComparison.Ordinal);
            StringAssert.Contains(delivery.TextContent, "Disabled", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll(".ops-attention-item"));
            Assert.IsFalse(cut.Markup.Contains("LogicHost connection unavailable", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void InitialFailure_RendersSanitizedErrorWithRetry()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unavailable, "Current operations data is unavailable."));

        var cut = context.Render<OperationsPage>();

        cut.WaitForAssertion(() =>
        {
            var failure = cut.Find(".operations-failure");
            Assert.AreEqual("alert", failure.GetAttribute("role"));
            StringAssert.Contains(failure.TextContent, "Operations unavailable.", StringComparison.Ordinal);
            Assert.AreEqual("Try again", failure.QuerySelector("button")!.TextContent);
            Assert.IsFalse(cut.Markup.Contains("Exception", StringComparison.Ordinal));
            Assert.IsEmpty(cut.FindAll("#capture-action"));
        });
    }

    [TestMethod]
    public void PauseConfirmation_PreventsDuplicateSubmitAndAnnouncesToast()
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
        Assert.AreEqual("Pause capture", cut.Find("#capture-action").TextContent);
        cut.Find("#capture-action").Click();
        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find("dialog.operations-dialog");
            Assert.AreEqual("Pause capture?", dialog.QuerySelector("#operations-dialog-heading")!.TextContent);
            StringAssert.Contains(dialog.QuerySelector(".operations-dialog-note")!.TextContent, "Expected state version 7.", StringComparison.Ordinal);
        });
        cut.Find("dialog footer .button.primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, calls);
            Assert.AreEqual(true, paused);
            Assert.AreEqual(7, version);
            var toast = cut.Find(".toast");
            Assert.AreEqual("status", toast.GetAttribute("role"));
            Assert.IsFalse(toast.HasAttribute("hidden"));
            Assert.AreEqual("Pause capture: Applied", toast.QuerySelector("strong")!.TextContent);
            Assert.AreEqual("Current state: Paused.", toast.QuerySelector("p")!.TextContent);
            Assert.IsEmpty(cut.FindAll("dialog"));
        });
    }

    [TestMethod]
    public void Toast_ClearsAfterItsLifetime()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        Configure(context);
        var time = new ManualTimeProvider(OperatorUiTestData.Now);
        context.Services.AddSingleton<TimeProvider>(time);

        var cut = context.Render<OperationsPage>();
        cut.Find("#capture-action").Click();
        cut.Find("dialog footer .button.primary").Click();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find(".toast").HasAttribute("hidden")));

        time.Advance(TimeSpan.FromSeconds(4));

        cut.WaitForAssertion(() => Assert.IsTrue(cut.Find(".toast").HasAttribute("hidden")));
    }

    [TestMethod]
    public void PausedCapture_OffersPrimaryResumeAndMarksDeck()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        var current = OperatorUiTestData.Operations();
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(WithCaptureState(current, "Paused")));
        bool? paused = null;
        service.CaptureHandler = (requestedPause, _, _, _) =>
        {
            paused = requestedPause;
            return Task.FromResult(OperatorUiResult<OperatorCommandReceipt>.Success(new(
                "Resume capture", "Applied", "Running", 8, OperatorUiTestData.Now)));
        };

        var cut = context.Render<OperationsPage>();

        cut.WaitForAssertion(() =>
        {
            var action = cut.Find("#capture-action");
            Assert.AreEqual("Resume capture", action.TextContent);
            Assert.IsTrue(action.ClassList.Contains("primary"));
            Assert.IsTrue(cut.Find(".ops-state-deck").ClassList.Contains("warning"));
            Assert.AreEqual("Capture paused", cut.Find(".ops-state-deck strong").TextContent);
        });
        cut.Find("#capture-action").Click();
        Assert.AreEqual("Resume capture?", cut.Find("#operations-dialog-heading").TextContent);
        cut.Find("dialog footer .button.primary").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(false, paused));
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
        cut.Find("dialog footer .button.primary").Click();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Showing last valid data.", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".ops-attention-list").TextContent, "Some operating facts are stale", StringComparison.Ordinal);
            var deck = cut.Find(".ops-state-deck");
            Assert.IsTrue(deck.ClassList.Contains("pending"));
            StringAssert.Contains(deck.TextContent, "Last observed state", StringComparison.Ordinal);
            StringAssert.Contains(cut.Markup, "Raw ingress", StringComparison.Ordinal);
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
        var commandTask = cut.Find("dialog footer .button.primary").TriggerEventAsync("onclick", EventArgs.Empty);
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        await cut.Instance.DisposeAsync().ConfigureAwait(false);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        await commandTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }

    [TestMethod]
    public void StaleSection_MarksDeckPendingAndRaisesAttention()
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
            Assert.IsTrue(cut.Find(".ops-state-deck").ClassList.Contains("pending"));
            StringAssert.Contains(cut.Find(".ops-attention-item.warning").TextContent, "Some operating facts are stale", StringComparison.Ordinal);
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
            StringAssert.Contains(cut.Find(".ops-attention-item").TextContent, "Some operating facts have not been observed", StringComparison.Ordinal);
            Assert.IsTrue(cut.Find(".ops-state-deck").ClassList.Contains("pending"));
            Assert.IsFalse(cut.Markup.Contains("No open items in the latest reading.", StringComparison.Ordinal));
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
            Assert.IsFalse(cut.Markup.Contains("LogicHost connection unavailable", StringComparison.Ordinal));
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
            AssertCard(cut, "/operations/schedule", "Schedule unavailable", "Open Schedule to load the revision");
            AssertCard(cut, "/operations/pipeline", "Pipeline unavailable", "Open Pipeline to load the plan");
            Assert.AreEqual("Unavailable", cut.FindAll(".ops-state-facts dd")[1].TextContent);
            StringAssert.Contains(cut.Find("[aria-labelledby='changes-heading'] .ops-panel-empty").TextContent, "Recent changes are unavailable.", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll(".ops-audit-list li"));
            Assert.IsFalse(cut.Find(".ops-state-deck").TextContent.Contains("setpoint", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void RigCatalog_ReportsPendingRestartAndFallsBackWhenUnavailable()
    {
        using (var context = new BunitContext())
        {
            Configure(context, new OverviewRigUiService { PendingRevisionId = "rig-v2" });
            var cut = context.Render<OperationsPage>();
            cut.WaitForAssertion(() => AssertCard(cut, "/operations/camera", "Installed rig / r1", "Revision r2 pending restart"));
        }

        using (var context = new BunitContext())
        {
            Configure(context, new OverviewRigUiService { Unavailable = true });
            var cut = context.Render<OperationsPage>();
            cut.WaitForAssertion(() =>
            {
                AssertCard(cut, "/operations/camera", "Rig catalog unavailable", "Open Camera & rig to load the inventory");
                Assert.AreEqual("Capture profile r2", cut.FindAll(".ops-state-facts dd")[2].TextContent);
            });
        }
    }

    [TestMethod]
    public void ActiveConfiguration_IsReadAgainOnceAMinute()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var rig = new OverviewRigUiService();
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(rig);
        var time = new ManualTimeProvider(OperatorUiTestData.Now);
        context.Services.AddSingleton<TimeProvider>(time);

        var cut = context.Render<OperationsPage>();
        cut.WaitForAssertion(() => AssertCard(cut, "/operations/camera", "Installed rig / r1", "Module Virtual Sky"));
        rig.PendingRevisionId = "rig-v2";
        var current = OperatorUiTestData.Operations();
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(WithCaptureState(current, "Paused")));

        time.Advance(TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Find(".ops-state-primary strong").TextContent, "paused", StringComparison.Ordinal));
        Assert.AreEqual(1, rig.Reads);
        AssertCard(cut, "/operations/camera", "Installed rig / r1", "Module Virtual Sky");

        time.Advance(TimeSpan.FromSeconds(55));
        cut.WaitForAssertion(() => AssertCard(cut, "/operations/camera", "Installed rig / r1", "Revision r2 pending restart"));
        Assert.AreEqual(2, rig.Reads);
    }

    [TestMethod]
    public void CalibrationActivation_AppearsInRecentChanges()
    {
        using var context = new BunitContext();
        Configure(context, calibration: new CalibrationUiStatus(3, null, null, 2, 0, null, null, null, null,
            new CalibrationLibraryActivationSnapshot("RollbackBundle", "bundle-2", "bundle-1", 3,
                OperatorUiTestData.Now.AddMinutes(-2), null)));
        var cut = context.Render<OperationsPage>();
        cut.WaitForAssertion(() =>
        {
            AssertCard(cut, "/operations/calibration", "No active bundle", "2 published bundles");
            var first = cut.Find(".ops-audit-list li");
            Assert.AreEqual("Calibration", first.QuerySelector("strong")!.TextContent);
            Assert.AreEqual("Rollback Bundle: bundle bundle-1 active.", first.QuerySelector("span")!.TextContent);
            Assert.AreEqual(OperatorUiTestData.Now.AddMinutes(-2).UtcDateTime.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                first.QuerySelector("time")!.TextContent);
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
        cut.WaitForAssertion(() =>
        {
            var link = cut.Find(".ops-state-deck a.button.primary");
            Assert.AreEqual("Latest run", link.TextContent);
            Assert.AreEqual($"/operations/pipeline/executions/{runId}", link.GetAttribute("href"));
            var processing = cut.FindAll(".ops-metric")[1];
            Assert.AreEqual("3.0 min", processing.QuerySelector("strong")!.TextContent);
            Assert.AreEqual("Completed run", processing.QuerySelector("small")!.TextContent);
        });
    }

    [TestMethod]
    public void InitializingCapture_DoesNotOfferPauseOrResume()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(WithCaptureState(OperatorUiTestData.Operations(), "Initializing")));
        var cut = context.Render<OperationsPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Capture initializing", cut.Find(".ops-state-deck strong").TextContent);
            StringAssert.Contains(cut.Markup, "pause and resume are unavailable in this state", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("#capture-action"));
            Assert.IsFalse(cut.Markup.Contains("command is settling", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void EnvironmentalDeliveryAttention_LinksToDataPage()
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
        cut.WaitForAssertion(() =>
        {
            var item = cut.Find(".ops-attention-item.info");
            StringAssert.Contains(item.TextContent, "One environmental delivery is retrying", StringComparison.Ordinal);
            Assert.AreEqual("/operations/delivery", item.QuerySelector("a")!.GetAttribute("href"));
        });
    }

    [TestMethod]
    public void QuarantinedArtifact_LinksToQuarantineWithoutInlineActions()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(OperatorUiTestData.Operations(
                artifactQuarantine: [new OperatorOutboxItem("Artifact", "Quarantined", "raw-ingress", "Preview",
                    1, 100, OperatorUiTestData.Now, "invalid-source", "replay-token", "abandon-token")])));
        var cut = context.Render<OperationsPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Review", cut.Find(".ops-attention-item a[href='/operations/quarantine']").TextContent);
            Assert.IsEmpty(cut.FindAll("button[id$='-abandon']"));
            Assert.IsEmpty(cut.FindAll("button[id$='-replay']"));
        });
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
        cut.Find("dialog footer .button.primary").Click();
        cut.WaitForAssertion(() =>
        {
            var error = cut.Find(".dialog-error");
            Assert.AreEqual("alert", error.GetAttribute("role"));
            StringAssert.Contains(error.TextContent, "could not be completed. Confirming again retries the same request.", StringComparison.Ordinal);
        });
        cut.Find("dialog footer .button.primary").Click();

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
    public void VersionConflict_RereadsStateAndRequiresANewlyReviewedCommand()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        var current = OperatorUiTestData.Operations();
        service.OperationsHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Success(current));
        var attempts = new List<(long Version, string Key)>();
        service.CaptureHandler = (_, expectedVersion, key, _) =>
        {
            attempts.Add((expectedVersion, key));
            if (attempts.Count == 1)
            {
                // Another actor paused and resumed capture while the dialog was open.
                current = WithCaptureControl(current, "Running", 9);
                return Task.FromResult(OperatorUiResult<OperatorCommandReceipt>.Failure(
                    OperatorUiResultKind.Conflict, "Capture state changed. Refresh and review the command again."));
            }
            return Task.FromResult(OperatorUiResult<OperatorCommandReceipt>.Success(new(
                "Pause capture", "Applied", "Paused", 10, OperatorUiTestData.Now)));
        };

        var cut = context.Render<OperationsPage>();
        cut.Find("#capture-action").Click();
        cut.Find("dialog footer .button.primary").Click();
        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find("dialog.operations-dialog");
            StringAssert.Contains(dialog.QuerySelector(".operations-dialog-note")!.TextContent, "Expected state version 9.", StringComparison.Ordinal);
            var error = dialog.QuerySelector(".dialog-error")!.TextContent;
            StringAssert.Contains(error, "Capture state changed while this was open. Capture is still Running", StringComparison.Ordinal);
            Assert.DoesNotContain("retries the same request", error);
        });
        cut.Find("dialog footer .button.primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(2, attempts);
            Assert.AreEqual(7, attempts[0].Version);
            Assert.AreEqual(9, attempts[1].Version);
            Assert.AreNotEqual(attempts[0].Key, attempts[1].Key);
            Assert.IsEmpty(cut.FindAll("dialog"));
            Assert.AreEqual("Pause capture: Applied", cut.Find(".toast strong").TextContent);
        });
    }

    [TestMethod]
    public void VersionConflict_ClosesWithoutResendingWhenTheRequestedStateIsAlreadyCurrent()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        var current = OperatorUiTestData.Operations();
        service.OperationsHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Success(current));
        var calls = 0;
        service.CaptureHandler = (_, _, _, _) =>
        {
            calls++;
            current = WithCaptureControl(current, "Paused", 8);
            return Task.FromResult(OperatorUiResult<OperatorCommandReceipt>.Failure(
                OperatorUiResultKind.Conflict, "Capture state changed. Refresh and review the command again."));
        };

        var cut = context.Render<OperationsPage>();
        cut.Find("#capture-action").Click();
        cut.Find("dialog footer .button.primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, calls);
            Assert.IsEmpty(cut.FindAll("dialog"));
            var toast = cut.Find(".toast");
            Assert.IsFalse(toast.HasAttribute("hidden"));
            Assert.AreEqual("status-icon warning", toast.QuerySelector(".status-icon")!.GetAttribute("class"));
            Assert.AreEqual("Capture state changed", toast.QuerySelector("strong")!.TextContent);
            Assert.AreEqual("Current state: Paused. The command was not sent again.", toast.QuerySelector("p")!.TextContent);
            Assert.AreEqual("Resume capture", cut.Find("#capture-action").TextContent);
        });
    }

    [TestMethod]
    public void AppliedCommand_StopsWhenTheVerificationReadIsDenied()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = Configure(context);
        var applied = false;
        service.OperationsHandler = _ => ValueTask.FromResult(applied
            ? OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unauthorized, "denied")
            : OperatorUiResult<CameraAgentOperationsView>.Success(OperatorUiTestData.Operations()));
        service.CaptureHandler = (_, _, _, _) =>
        {
            applied = true;
            return Task.FromResult(OperatorUiResult<OperatorCommandReceipt>.Success(new(
                "Pause capture", "Applied", "Paused", 8, OperatorUiTestData.Now)));
        };
        var navigation = context.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        var cut = context.Render<OperationsPage>();
        cut.Find("#capture-action").Click();
        cut.Find("dialog footer .button.primary").Click();

        cut.WaitForAssertion(() => StringAssert.EndsWith(navigation.Uri, "/Account/AccessDenied", StringComparison.Ordinal));
        Assert.IsTrue(cut.Find(".toast").HasAttribute("hidden"));
        Assert.IsEmpty(cut.FindAll("dialog"));
        Assert.IsEmpty(cut.FindAll("#capture-action"));
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
        var commandTask = cut.Find("dialog footer .button.primary").TriggerEventAsync("onclick", EventArgs.Empty);
        await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var refreshTask1 = cut.InvokeAsync(cut.Instance.RefreshNowAsync);
        var refreshTask2 = cut.InvokeAsync(cut.Instance.RefreshNowAsync);
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
        cut.Find("dialog.operations-dialog").TriggerEvent("oncancel", EventArgs.Empty);

        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(cut.FindAll("dialog"));
            Assert.IsTrue(context.JSInterop.Invocations.Any(static invocation => invocation.Identifier == "close"));
            Assert.IsEmpty(cut.FindAll("main"));
        });
    }

    private static void AssertCard(IRenderedComponent<OperationsPage> cut, string href, string title, string detail)
    {
        var card = cut.Find($".ops-config-card[href='{href}']");
        Assert.AreEqual(title, card.QuerySelector("strong")!.TextContent);
        Assert.AreEqual(detail, card.QuerySelector("small")!.TextContent);
    }

    private static CameraAgentOperationsView WithCaptureControl(CameraAgentOperationsView current, string state, long version) => current with
    {
        Summary = current.Summary with
        {
            CaptureControl = current.Summary.CaptureControl with
            {
                Value = current.Summary.CaptureControl.Value with { State = state, Version = version }
            }
        }
    };

    private static CameraAgentOperationsView WithCaptureState(CameraAgentOperationsView current, string state) => current with
    {
        Summary = current.Summary with
        {
            CaptureControl = current.Summary.CaptureControl with
            {
                Value = current.Summary.CaptureControl.Value with { State = state }
            }
        }
    };

    internal static TestOperatorUiService ConfigureOverview(BunitContext context) => Configure(context);

    private static TestOperatorUiService Configure(
        BunitContext context,
        OverviewRigUiService? rig = null,
        CalibrationUiStatus? calibration = null)
    {
        var service = new TestOperatorUiService();
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(service);
        context.Services.AddSingleton<ICameraAgentScheduleUiService>(
            new SchedulePageTests.ScheduleUiService(SchedulePageTests.State()));
        context.Services.AddSingleton<ICameraAgentCalibrationUiService>(new OverviewCalibrationUiService(
            calibration ?? new CalibrationUiStatus(1, null, null, 0, 0, null, null, null, null, null)));
        context.Services.AddSingleton<ICameraAgentNamedRigUiService>(rig ?? new OverviewRigUiService());
        context.Services.AddSingleton<ICameraAgentProcessingGraphUiService>(new ProcessingExecutionPagesTests.GraphUiService
        {
            Executions = new CameraAgentProcessingExecutionsView(OperatorUiTestData.Now, 1, [], [])
        });
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(OperatorUiTestData.Now));
        return service;
    }

    private sealed class UnavailableScheduleUiService : ICameraAgentScheduleUiService
    {
        public ValueTask<OperatorUiResult<CaptureScheduleOperatorState>> GetAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CaptureScheduleOperatorState>
                .Failure(OperatorUiResultKind.Unavailable, "Schedule unavailable."));

        public ValueTask<OperatorUiResult<CaptureSchedulePreview>> PreviewAsync(
            string profileJson, string basisRevisionId, int dayCount, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> StageAsync(
            string profileJson, string basisRevisionId, long expectedVersion, string idempotencyKey, string? reason,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ActivateAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> RollbackAsync(
            string revisionId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> AddOverrideAsync(
            CaptureScheduleOverride scheduleOverride, long expectedVersion,
            string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CaptureScheduleStoreSnapshot>> ClearOverrideAsync(
            string overrideId, long expectedVersion, string idempotencyKey, string? reason, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class OverviewCalibrationUiService(CalibrationUiStatus status) : ICameraAgentCalibrationUiService
    {
        public ValueTask<OperatorUiResult<CalibrationUiStatus>> GetStatusAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(OperatorUiResult<CalibrationUiStatus>.Success(status));

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

    /// <summary>The read side of the named-rig service the overview uses; every mutation is out of scope here.</summary>
    private sealed class OverviewRigUiService : ICameraAgentNamedRigUiService
    {
        private int _reads;

        internal string? PendingRevisionId { get; set; }
        internal bool Unavailable { get; init; }
        internal int Reads => Volatile.Read(ref _reads);

        public ValueTask<OperatorUiResult<NamedRigUiCatalog>> GetAsync(CancellationToken token)
        {
            Interlocked.Increment(ref _reads);
            if (Unavailable)
            {
                return ValueTask.FromResult(OperatorUiResult<NamedRigUiCatalog>.Failure(
                    OperatorUiResultKind.Unavailable, "Catalog unavailable"));
            }
            var profile = SchedulePageTests.Profile();
            var installed = new NamedRigRevision("rig-v1", "rig", 1, "camera-v1", "optics-v1", "mount-v1",
                profile.Module, profile.Rig, null);
            return ValueTask.FromResult(OperatorUiResult<NamedRigUiCatalog>.Success(new(
                new NamedRigSelection("rig-v1", PendingRevisionId, 3),
                [installed, installed with { RevisionId = "rig-v2", RevisionNumber = 2 }],
                null,
                null)));
        }

        public ValueTask<OperatorUiResult<NamedRigInventory>> GetInventoryAsync(CancellationToken token)
            => ValueTask.FromResult(Unavailable
                ? OperatorUiResult<NamedRigInventory>.Failure(OperatorUiResultKind.Unavailable, "Inventory unavailable")
                : OperatorUiResult<NamedRigInventory>.Success(new([new NamedRigProfile("rig", "Installed rig")], [])));

        public ValueTask<OperatorUiResult<NamedRigHistoryPage>> GetHistoryAsync(string profileId, int limit,
            long? beforeRevisionNumber, long? version, CancellationToken token) => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<NamedEquipmentDetail>> GetEquipmentAsync(string revisionId, CancellationToken token)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<NamedEquipmentDefinition>> SaveEquipmentAsync(string? definitionId, string kind,
            string name, JsonElement definition, string? basisRevisionId, string? expectedRevisionId, CancellationToken token)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<NamedEquipmentDefinition>> CreateZwoStarterAsync(string templateId, string name,
            CancellationToken token) => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<NamedRigProfile>> SaveProfileAsync(string? profileId, string name,
            CancellationToken token) => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<NamedRigRevision>> ComposeAsync(string profileId, string cameraId,
            string opticsId, string mountId, CancellationToken token) => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<NamedRigPreview>> PreviewAsync(string revisionId, CancellationToken token)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<NamedRigStageReceipt>> StageAsync(string revisionId, long version, string key,
            bool acknowledgeUnvalidated, string expectedScheduleRevisionId, string expectedScheduleProfileSha256,
            CancellationToken token) => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<NamedRigStageReceipt>> CancelAsync(string revisionId, long version, string key,
            CancellationToken token) => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<ActiveRigEditOutcome>> ApplyActiveRigEditAsync(ActiveRigEditRequest request,
            CancellationToken token) => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CameraAgentRestartStatus>> GetRestartStatusAsync(CancellationToken token)
            => throw new NotSupportedException();

        public ValueTask<OperatorUiResult<CameraAgentRestartDisposition>> RequestRestartAsync(CancellationToken token)
            => throw new NotSupportedException();
    }

    /// <summary>A clock whose timers fire only when a test advances it.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        internal void Advance(TimeSpan duration)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _now += duration;
                due = _timers.Where(timer => timer.DueUtc <= _now).ToArray();
                foreach (var timer in due)
                {
                    if (timer.Period > TimeSpan.Zero && timer.Period != Timeout.InfiniteTimeSpan)
                    {
                        timer.DueUtc = _now + timer.Period;
                    }
                    else
                    {
                        _timers.Remove(timer);
                    }
                }
            }
            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    timer.DueUtc = _now + dueTime;
                    timer.Period = period;
                    _timers.Add(timer);
                }
            }
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            internal DateTimeOffset DueUtc { get; set; }
            internal TimeSpan Period { get; set; }

            internal void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                owner.Schedule(this, dueTime, period);
                return true;
            }

            public void Dispose() => owner.Schedule(this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
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
