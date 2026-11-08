using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

public sealed partial class ControlPageTests
{
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
