using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class ControlPageTests
{
    [TestMethod]
    [DataRow(CameraAgentControlPhase.Draining, "Stopping acquisition and settling accepted processing and delivery.")]
    [DataRow(CameraAgentControlPhase.Restarting, "Installer requested a supervised CameraAgent restart.")]
    [DataRow(CameraAgentControlPhase.Verifying, "Installer is waiting for a new process and verifying durable continuity.")]
    public void ClaimedLifecycleShowsHeldAdmissionAndItsPhaseWithFreshWorkerLease(CameraAgentControlPhase phase, string detail)
    {
        using var context = new BunitContext();
        var (_, system, _) = Configure(context);
        var receipt = DisplayReceipt(phase);
        system.LifecycleHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentControlView>.Success(
            new(true, [receipt], WorkerLeaseExpired: false)));
        var cut = context.Render<ControlPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Capture admission is held for installer lifecycle control.", cut.Find(".ops-note-banner strong").TextContent);
            var progress = cut.Find("section[aria-labelledby='control-receipts-heading'] .ops-note-banner");
            Assert.AreEqual(detail, progress.QuerySelector("span")!.TextContent);
            Assert.IsNotNull(progress.QuerySelector("#control-lifecycle-cancel"));
            AssertDrainUnavailable(cut, "An installer lifecycle request is active. Wait for its terminal receipt before requesting another drain.");
        });
    }

    [TestMethod]
    [DataRow("Running")]
    [DataRow("Paused")]
    public void EnabledDrainHasNoUnavailableDescription(string state)
    {
        using var context = new BunitContext();
        var (operations, system, _) = Configure(context);
        operations.OperationsHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Success(WithCapture(state, 7)));
        system.LifecycleHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentControlView>.Success(new(true, [])));
        var cut = context.Render<ControlPage>();

        cut.WaitForAssertion(() => AssertDrainAvailable(cut));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UnavailableWorkerExplainsDrainAndFreshLeaseClearsTheDescription(bool expired)
    {
        using var context = new BunitContext();
        var (_, system, _) = Configure(context);
        var lifecycle = new CameraAgentControlView(false, [], WorkerLeaseExpired: expired);
        system.LifecycleHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentControlView>.Success(lifecycle));
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => AssertDrainUnavailable(cut,
            "The authenticated installer lifecycle worker is not registered or its heartbeat expired. Pause and resume remain available."));

        lifecycle = new(true, []);
        cut.Find("#control-refresh").Click();
        cut.WaitForAssertion(() => AssertDrainAvailable(cut));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void QueuedLifecycleExplainsDrainEvenWhenWorkerLeaseExpires(bool expired)
    {
        using var context = new BunitContext();
        var (_, system, _) = Configure(context);
        system.LifecycleHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentControlView>.Success(
            new(!expired, [DisplayReceipt(CameraAgentControlPhase.Queued)], WorkerLeaseExpired: expired)));
        var cut = context.Render<ControlPage>();

        cut.WaitForAssertion(() =>
        {
            AssertDrainUnavailable(cut, "An installer lifecycle request is active. Wait for its terminal receipt before requesting another drain.");
            Assert.IsFalse(cut.Find("#control-capture").HasAttribute("disabled"));
            Assert.IsNotNull(cut.Find("#control-lifecycle-cancel"));
        });
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PendingStopExplainsDrainEvenWhenWorkerOrRequestAlsoBlocksIt(bool activeRequest)
    {
        using var context = new BunitContext();
        var (_, system, _) = Configure(context);
        system.LifecycleHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentControlView>.Success(new(false,
            activeRequest ? [DisplayReceipt(CameraAgentControlPhase.Restarting)] : [], StopPending: true)));
        var cut = context.Render<ControlPage>();

        cut.WaitForAssertion(() => AssertDrainUnavailable(cut,
            "A restart stop is still pending in this process. Wait for CameraAgent to restart."));
    }

    [TestMethod]
    [DataRow("PauseRequested")]
    [DataRow("Initializing")]
    [DataRow("Unavailable")]
    public void IneligibleCaptureExplainsDrainUntilStateBecomesPaused(string state)
    {
        using var context = new BunitContext();
        var (operations, system, _) = Configure(context);
        operations.OperationsHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Success(WithCapture(state, 7)));
        system.LifecycleHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentControlView>.Success(new(true, [])));
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => AssertDrainUnavailable(cut, "Capture must be Running or Paused before requesting a drain."));

        state = "Paused";
        cut.Find("#control-refresh").Click();
        cut.WaitForAssertion(() => AssertDrainAvailable(cut));
    }

    [TestMethod]
    [DataRow(false, "The capture state could not be read. Refresh to try again.")]
    [DataRow(true, "Installer lifecycle availability could not be read. Refresh to try again.")]
    public void FailedStateReadExplainsDrainAndRefreshRecovers(bool lifecycleReadFails, string expected)
    {
        using var context = new BunitContext();
        var (operations, system, _) = Configure(context);
        var unavailable = true;
        operations.OperationsHandler = _ => ValueTask.FromResult(unavailable && !lifecycleReadFails
            ? OperatorUiResult<CameraAgentOperationsView>.Failure(OperatorUiResultKind.Unavailable, "Capture read failed.")
            : OperatorUiResult<CameraAgentOperationsView>.Success(WithCapture("Running", 7)));
        system.LifecycleHandler = _ => ValueTask.FromResult(unavailable && lifecycleReadFails
            ? OperatorUiResult<CameraAgentControlView>.Failure(OperatorUiResultKind.Unavailable, "Lifecycle read failed.")
            : OperatorUiResult<CameraAgentControlView>.Success(new(true, [])));
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => AssertDrainUnavailable(cut, expected));

        unavailable = false;
        cut.Find("#control-refresh").Click();
        cut.WaitForAssertion(() => AssertDrainAvailable(cut));
    }

    [TestMethod]
    [DataRow(CameraAgentControlPhase.Succeeded)]
    [DataRow(CameraAgentControlPhase.Failed)]
    [DataRow(CameraAgentControlPhase.Cancelled)]
    [DataRow(CameraAgentControlPhase.Skipped)]
    public void TerminalReceiptRefreshRemovesActiveProgressAndDrainDescription(CameraAgentControlPhase phase)
    {
        using var context = new BunitContext();
        var (operations, system, _) = Configure(context);
        operations.OperationsHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Success(WithCapture("Paused", 7)));
        var receipt = DisplayReceipt(CameraAgentControlPhase.Draining);
        system.LifecycleHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentControlView>.Success(new(true, [receipt])));
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => Assert.IsNotNull(cut.Find("#control-lifecycle-cancel")));

        receipt = DisplayReceipt(phase);
        cut.Find("#control-refresh").Click();
        cut.WaitForAssertion(() =>
        {
            AssertDrainAvailable(cut);
            Assert.IsEmpty(cut.FindAll("#control-lifecycle-cancel"));
            Assert.AreEqual("Capture is paused.", cut.Find(".ops-note-banner strong").TextContent);
            Assert.AreEqual("Resume", cut.Find("#control-capture").TextContent);
        });
    }

    [TestMethod]
    public async Task SubmittingDrainExplainsTemporaryDisableAndFailedSubmissionAllowsRetry()
    {
        using var context = new BunitContext();
        var (_, system, _) = Configure(context);
        system.LifecycleHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentControlView>.Success(new(true, [])));
        var result = new TaskCompletionSource<OperatorUiResult<CameraAgentControlReceipt>>(TaskCreationOptions.RunContinuationsAsynchronously);
        system.DrainHandler = (_, _, _, _) => new(result.Task);
        var cut = context.Render<ControlPage>();
        cut.WaitForElement("#control-drain").Click();
        cut.Find("#control-reason").Change("maintenance");
        var submission = cut.Find("#control-dialog-confirm").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => AssertDrainUnavailable(cut, "A control request is being submitted. Wait for it to finish."));

        result.SetResult(OperatorUiResult<CameraAgentControlReceipt>.Failure(OperatorUiResultKind.Unavailable, "The installer could not accept the request."));
        await submission;
        cut.WaitForAssertion(() =>
        {
            AssertDrainAvailable(cut);
            StringAssert.Contains(cut.Find(".dialog-error").TextContent, "The installer could not accept the request.", StringComparison.Ordinal);
            Assert.IsFalse(cut.Find("#control-dialog-confirm").HasAttribute("disabled"));
        });
    }

    private static CameraAgentControlReceipt DisplayReceipt(CameraAgentControlPhase phase)
    {
        var kind = phase is CameraAgentControlPhase.Restarting or CameraAgentControlPhase.Verifying or CameraAgentControlPhase.Skipped
            ? CameraAgentControlKind.Restart : CameraAgentControlKind.Drain;
        var request = new CameraAgentControlRequest(Guid.NewGuid(), kind, "owner", "maintenance", 7, OperatorUiTestData.Now);
        var events = new List<CameraAgentControlEvent> { new(CameraAgentControlPhase.Queued, request.RequestedUtc, "lifecycle.queued") };
        if (phase != CameraAgentControlPhase.Queued)
        {
            events.Add(new(CameraAgentControlPhase.Draining, request.RequestedUtc, "lifecycle.draining"));
            if (phase is CameraAgentControlPhase.Restarting or CameraAgentControlPhase.Verifying)
                events.Add(new(CameraAgentControlPhase.Restarting, request.RequestedUtc, "lifecycle.restarting"));
            if (phase != CameraAgentControlPhase.Draining && phase != CameraAgentControlPhase.Restarting)
                events.Add(new(phase, request.RequestedUtc, "lifecycle.display-test"));
        }
        var receipt = new CameraAgentControlReceipt(request, phase, events, PausedVersion: 7, DrainedCaptureSequence: 1);
        Assert.IsTrue(receipt.IsValid());
        return receipt;
    }

    private static void AssertDrainUnavailable(IRenderedComponent<ControlPage> cut, string expected)
    {
        var button = cut.Find("#control-drain");
        Assert.IsTrue(button.HasAttribute("disabled"));
        Assert.AreEqual("control-drain-unavailable", button.GetAttribute("aria-describedby"));
        Assert.AreEqual(expected, button.GetAttribute("title"));
        Assert.AreEqual(expected, cut.Find("#control-drain-unavailable").TextContent);
    }

    private static void AssertDrainAvailable(IRenderedComponent<ControlPage> cut)
    {
        var button = cut.Find("#control-drain");
        Assert.IsFalse(button.HasAttribute("disabled"));
        Assert.IsFalse(button.HasAttribute("aria-describedby"));
        Assert.IsFalse(button.HasAttribute("title"));
        Assert.IsEmpty(cut.FindAll("#control-drain-unavailable"));
    }

    [TestMethod]
    public void TerminalReceiptRefreshReadsAdmissionAfterPublicationRatherThanKeepingEarlierRunningState()
    {
        using var context = new BunitContext();
        var (operations, system, _) = Configure(context);
        var state = "Running";
        operations.OperationsHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Success(WithCapture(state, 7)));
        system.LifecycleHandler = _ =>
        {
            state = "Paused"; // Installer settles an ambiguous resume after the first summary was read.
            var request = new CameraAgentControlRequest(Guid.NewGuid(), CameraAgentControlKind.Restart, "owner", "maintenance", 7, OperatorUiTestData.Now);
            return ValueTask.FromResult(OperatorUiResult<CameraAgentControlView>.Success(new(true,
                [new(request, CameraAgentControlPhase.Failed, [new(CameraAgentControlPhase.Failed, request.RequestedUtc, "restart.resume-unconfirmed")])])));
        };
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Resume", cut.Find("#control-capture").TextContent);
            StringAssert.Contains(cut.Markup, "Capture is paused.", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void DrainRequiresRegisteredInstallerAndConfirmedReason_OpeningAndCancellingDoNotMutate()
    {
        using var context = new BunitContext();
        var (_, system, _) = Configure(context);
        system.LifecycleHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentControlView>.Success(new(true, [])));
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#control-drain").HasAttribute("disabled")));
        cut.Find("#control-drain").Click();
        StringAssert.Contains(cut.Find("dialog").TextContent, "Reason (required", StringComparison.Ordinal);
        cut.Find("#control-dialog-confirm").Click();
        Assert.IsEmpty(system.DrainRequests);
        StringAssert.Contains(cut.Find("dialog").TextContent, "A reason", StringComparison.Ordinal);
        cut.Find("#control-dialog-cancel").Click();
        Assert.IsEmpty(system.DrainRequests);
    }

    [TestMethod]
    public void DrainSubmitsTrimmedReasonAndVersion_ThenShowsPendingInstallerProgress()
    {
        using var context = new BunitContext();
        var (_, system, _) = Configure(context);
        CameraAgentControlReceipt? receipt = null;
        system.LifecycleHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentControlView>.Success(new(true,
            receipt is null ? [] : [receipt])));
        system.DrainHandler = (id, version, reason, _) =>
        {
            var request = new CameraAgentControlRequest(id, CameraAgentControlKind.Drain, "owner", reason, version, OperatorUiTestData.Now);
            receipt = new(request, CameraAgentControlPhase.Queued,
                [new(CameraAgentControlPhase.Queued, request.RequestedUtc, "lifecycle.queued")]);
            return ValueTask.FromResult(OperatorUiResult<CameraAgentControlReceipt>.Success(receipt));
        };
        var cut = context.Render<ControlPage>();
        cut.WaitForElement("#control-drain").Click();
        cut.Find("#control-reason").Change("  planned maintenance  ");
        cut.Find("#control-dialog-confirm").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(1, system.DrainRequests);
            Assert.AreEqual("planned maintenance", system.DrainRequests[0].Reason);
            Assert.AreNotEqual(Guid.Empty, system.DrainRequests[0].Id);
            Assert.IsTrue(cut.Find("#control-drain").HasAttribute("disabled"));
            StringAssert.Contains(cut.Markup, "Waiting for the installer worker", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("dialog"));
        });
        cut.Find("#control-lifecycle-cancel").Click();
        Assert.IsEmpty(system.CancelRequests, "Cancellation must carry an operator reason.");
        cut.Find("#control-cancel-reason").Change("Maintenance no longer needed");
        cut.Find("#control-lifecycle-cancel").Click();
        cut.WaitForAssertion(() => Assert.AreEqual(receipt!.Request.OperationId, system.CancelRequests.Single()));
    }

    [TestMethod]
    public void DrainIsDisabledWithoutContractAndPauseRemainsAvailable()
    {
        using var context = new BunitContext();
        Configure(context);
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Find("#control-drain").HasAttribute("disabled"));
            Assert.IsFalse(cut.Find("#control-capture").HasAttribute("disabled"));
            StringAssert.Contains(cut.Find("#control-drain").GetAttribute("title"), "installer lifecycle worker", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void ExpiredWorkerWithQueuedRequestOffersCancellationAndOrdinaryResume()
    {
        using var context = new BunitContext();
        var (operations, system, _) = Configure(context);
        operations.OperationsHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Success(WithCapture("Paused", 7)));
        var request = new CameraAgentControlRequest(Guid.NewGuid(), CameraAgentControlKind.Drain, "owner", "maintenance", 7, OperatorUiTestData.Now);
        system.LifecycleHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentControlView>.Success(new(false,
            [new(request, CameraAgentControlPhase.Queued, [new(CameraAgentControlPhase.Queued, request.RequestedUtc, "lifecycle.queued")])], WorkerLeaseExpired: true)));
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Worker lease expired with active request", StringComparison.Ordinal);
            Assert.AreEqual("Resume", cut.Find("#control-capture").TextContent);
            Assert.IsFalse(cut.Find("#control-capture").HasAttribute("disabled"));
            Assert.IsNotNull(cut.Find("#control-lifecycle-cancel"));
        });
    }

    [TestMethod]
    public void TerminalCancellationWithArmedStopKeepsResumeDisabledAndExplainsFence()
    {
        using var context = new BunitContext();
        var (operations, system, _) = Configure(context);
        operations.OperationsHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Success(WithCapture("Paused", 7)));
        system.LifecycleHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentControlView>.Success(new(false, [], StopPending: true)));
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "restart stop is still pending", StringComparison.Ordinal);
            Assert.IsTrue(cut.Find("#control-capture").HasAttribute("disabled"));
            Assert.IsTrue(cut.Find("#control-drain").HasAttribute("disabled"));
        });
    }

    [TestMethod]
    public void FailedInstallerReceiptIsVisibleWithItsReasonAndFailureState()
    {
        using var context = new BunitContext();
        var (_, system, _) = Configure(context);
        system.ReceiptsHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<IReadOnlyList<SystemControlReceipt>>.Success(
            [new(OperatorUiTestData.Now, SystemControlReceiptKind.ScheduledRestart, SystemControlActor.Installer,
                "planned maintenance", SystemControlOutcome.Failed, null, null, null,
                "Verification window expired. CameraAgent did not return; no automatic resume was issued.")]));
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find(".control-receipts").TextContent, "Scheduled restart", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".control-receipts").TextContent, "Failed", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".control-receipts").TextContent, "planned maintenance", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find(".control-receipts").TextContent, "did not return", StringComparison.Ordinal);
        });
    }
}
