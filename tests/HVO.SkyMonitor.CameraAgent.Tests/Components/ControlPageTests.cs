using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class ControlPageTests
{
    [TestMethod]
    public void Pause_SendsTheTrimmedReasonAndShowsTheReceipt()
    {
        using var context = new BunitContext();
        var (operations, system, _) = Configure(context);
        var state = "Running";
        long? sentVersion = null;
        string? sentKey = null;
        operations.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(WithCapture(state, state == "Running" ? 7 : 8)));
        operations.CaptureHandler = (paused, version, key, _) =>
        {
            sentVersion = version;
            sentKey = key;
            state = paused ? "Paused" : "Running";
            return Task.FromResult(OperatorUiResult<OperatorCommandReceipt>.Success(new(
                "Pause capture", "Applied", "Paused", 8, OperatorUiTestData.Now)));
        };
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => Assert.AreEqual("Pause", cut.Find("#control-capture").TextContent));
        StringAssert.Contains(cut.Markup, "Capture is not paused.", StringComparison.Ordinal);

        cut.Find("#control-capture").Click();
        Assert.AreEqual("Pause acquisition?", cut.Find("#control-dialog-heading").TextContent);
        StringAssert.Contains(cut.Find("dialog").TextContent, "Expected state version 7.", StringComparison.Ordinal);
        cut.Find("#control-reason").Change("  Cleaning the dome  ");
        cut.Find("#control-dialog-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            string?[] expectedReasons = ["Cleaning the dome"];
            CollectionAssert.AreEqual(expectedReasons, operations.CaptureReasons);
            Assert.AreEqual(7L, sentVersion);
            StringAssert.StartsWith(sentKey, "ui-", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("dialog"));
            var message = cut.Find(".control-message").TextContent;
            StringAssert.Contains(message, "Pause capture: Applied.", StringComparison.Ordinal);
            StringAssert.Contains(message, "Current state: Paused.", StringComparison.Ordinal);
            Assert.AreEqual("Resume", cut.Find("#control-capture").TextContent);
            Assert.AreEqual(2, system.ReceiptReads);
        });
    }

    [TestMethod]
    public void Paused_OffersResumeWithoutAReason()
    {
        using var context = new BunitContext();
        var (operations, _, _) = Configure(context);
        operations.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(WithCapture("Paused", 8)));
        bool? requested = null;
        operations.CaptureHandler = (paused, _, _, _) =>
        {
            requested = paused;
            return Task.FromResult(OperatorUiResult<OperatorCommandReceipt>.Success(new(
                "Resume capture", "Applied", "Running", 9, OperatorUiTestData.Now)));
        };
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => Assert.AreEqual("Resume", cut.Find("#control-capture").TextContent));
        StringAssert.Contains(cut.Markup, "Capture is paused.", StringComparison.Ordinal);
        StringAssert.Contains(cut.Find("#control-capture").ClassName, "primary", StringComparison.Ordinal);

        cut.Find("#control-capture").Click();
        Assert.AreEqual("Resume acquisition?", cut.Find("#control-dialog-heading").TextContent);
        cut.Find("#control-dialog-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsFalse(requested);
            string?[] expectedReasons = [null];
            CollectionAssert.AreEqual(expectedReasons, operations.CaptureReasons);
        });
    }

    [TestMethod]
    public void FailedAttempt_KeepsTheKeyUntilTheReasonChanges()
    {
        using var context = new BunitContext();
        var (operations, _, _) = Configure(context);
        var keys = new List<string>();
        operations.CaptureHandler = (_, _, key, _) =>
        {
            keys.Add(key);
            return Task.FromResult(keys.Count < 3
                ? OperatorUiResult<OperatorCommandReceipt>.Failure(OperatorUiResultKind.Unavailable, "The capture state store is busy.")
                : OperatorUiResult<OperatorCommandReceipt>.Success(new("Pause capture", "Applied", "Paused", 8, OperatorUiTestData.Now)));
        };
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#control-capture").HasAttribute("disabled")));
        cut.Find("#control-capture").Click();
        cut.Find("#control-reason").Change("Lens cleaning");

        cut.Find("#control-dialog-confirm").Click();
        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Find(".dialog-error").TextContent, "The capture state store is busy. Confirming again retries the same request.", StringComparison.Ordinal));
        cut.Find("#control-dialog-confirm").Click();
        cut.WaitForAssertion(() => Assert.HasCount(2, keys));
        cut.Find("#control-reason").Change("Lens cleaning and focus check");
        cut.Find("#control-dialog-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(3, keys);
            Assert.AreEqual(keys[0], keys[1]);
            Assert.AreNotEqual(keys[1], keys[2]);
            Assert.IsEmpty(cut.FindAll("dialog"));
        });
    }

    [TestMethod]
    public void Conflict_WhileStillRunning_RearmsWithTheNewVersionAndKey()
    {
        using var context = new BunitContext();
        var (operations, _, _) = Configure(context);
        var reads = 0;
        operations.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(WithCapture("Running", ++reads == 1 ? 7 : 9)));
        var attempts = new List<(long Version, string Key)>();
        operations.CaptureHandler = (_, version, key, _) =>
        {
            attempts.Add((version, key));
            return Task.FromResult(attempts.Count == 1
                ? OperatorUiResult<OperatorCommandReceipt>.Failure(OperatorUiResultKind.Conflict, "Version mismatch.")
                : OperatorUiResult<OperatorCommandReceipt>.Success(new("Pause capture", "Applied", "Paused", 10, OperatorUiTestData.Now)));
        };
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#control-capture").HasAttribute("disabled")));
        cut.Find("#control-capture").Click();

        cut.Find("#control-dialog-confirm").Click();
        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find(".dialog-error").TextContent, "Capture is still running", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find("dialog").TextContent, "Expected state version 9.", StringComparison.Ordinal);
        });
        cut.Find("#control-dialog-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(2, attempts);
            Assert.AreEqual(7L, attempts[0].Version);
            Assert.AreEqual(9L, attempts[1].Version);
            Assert.AreNotEqual(attempts[0].Key, attempts[1].Key);
        });
    }

    [TestMethod]
    public void Conflict_WhenAlreadyPaused_ClosesWithoutSendingAgain()
    {
        using var context = new BunitContext();
        var (operations, _, _) = Configure(context);
        var reads = 0;
        operations.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(++reads == 1 ? WithCapture("Running", 7) : WithCapture("Paused", 8)));
        var calls = 0;
        operations.CaptureHandler = (_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(OperatorUiResult<OperatorCommandReceipt>.Failure(OperatorUiResultKind.Conflict, "Version mismatch."));
        };
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#control-capture").HasAttribute("disabled")));
        cut.Find("#control-capture").Click();

        cut.Find("#control-dialog-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(1, calls);
            Assert.IsEmpty(cut.FindAll("dialog"));
            var message = cut.Find(".control-message").TextContent;
            StringAssert.Contains(message, "Capture state changed.", StringComparison.Ordinal);
            StringAssert.Contains(message, "Current state: Paused. The command was not sent again.", StringComparison.Ordinal);
            Assert.AreEqual("Resume", cut.Find("#control-capture").TextContent);
        });
    }

    [TestMethod]
    public void OverLongReason_IsRefusedBeforeSending()
    {
        using var context = new BunitContext();
        var (operations, _, _) = Configure(context);
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#control-capture").HasAttribute("disabled")));
        cut.Find("#control-capture").Click();
        Assert.AreEqual("512", cut.Find("#control-reason").GetAttribute("maxlength"));
        cut.Find("#control-reason").Change(new string('x', ControlPage.ReasonMaxLength + 1));

        cut.Find("#control-dialog-confirm").Click();

        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Find(".dialog-error").TextContent, "The reason must be 512 characters or fewer.", StringComparison.Ordinal));
        Assert.IsEmpty(operations.CaptureReasons);
    }

    [TestMethod]
    public void ClosingTheDialog_DiscardsTheIdempotencyKey()
    {
        using var context = new BunitContext();
        var (operations, _, _) = Configure(context);
        var keys = new List<string>();
        operations.CaptureHandler = (_, _, key, _) =>
        {
            keys.Add(key);
            return Task.FromResult(OperatorUiResult<OperatorCommandReceipt>.Failure(OperatorUiResultKind.Unavailable, "Busy."));
        };
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#control-capture").HasAttribute("disabled")));
        cut.Find("#control-capture").Click();
        cut.Find("#control-dialog-confirm").Click();
        cut.WaitForAssertion(() => Assert.HasCount(1, keys));

        cut.Find("#control-dialog-cancel").Click();
        Assert.IsEmpty(cut.FindAll("dialog"));
        cut.Find("#control-capture").Click();
        Assert.IsEmpty(cut.FindAll(".dialog-error"));
        cut.Find("#control-dialog-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.HasCount(2, keys);
            Assert.AreNotEqual(keys[0], keys[1]);
        });
    }

    [TestMethod]
    public void UnreadableState_DisablesPauseAndSaysWhy()
    {
        using var context = new BunitContext();
        var (operations, _, _) = Configure(context);
        operations.OperationsHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Failure(
            OperatorUiResultKind.Unavailable, "The operations summary is unavailable."));

        var cut = context.Render<ControlPage>();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Find("[role='alert']").TextContent, "Capture state unavailable.", StringComparison.Ordinal);
            var trigger = cut.Find("#control-capture");
            Assert.IsTrue(trigger.HasAttribute("disabled"));
            Assert.AreEqual("control-capture-unavailable", trigger.GetAttribute("aria-describedby"));
            Assert.AreEqual(
                "The capture state could not be read. Refresh to try again.", cut.Find("#control-capture-unavailable").TextContent);
        });
    }

    [TestMethod]
    public void PauseRequested_ShowsPausingAndDisablesTheButton()
    {
        using var context = new BunitContext();
        var (operations, _, _) = Configure(context);
        operations.OperationsHandler = _ => ValueTask.FromResult(
            OperatorUiResult<CameraAgentOperationsView>.Success(WithCapture("PauseRequested", 8)));

        var cut = context.Render<ControlPage>();

        cut.WaitForAssertion(() =>
        {
            var trigger = cut.Find("#control-capture");
            Assert.AreEqual("Pausing…", trigger.TextContent);
            Assert.IsTrue(trigger.HasAttribute("disabled"));
            StringAssert.Contains(cut.Markup, "Capture is pausing.", StringComparison.Ordinal);
            StringAssert.StartsWith(cut.Find("#control-capture-unavailable").TextContent, "A pause is in progress", StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void Restart_OnASupervisedHost_ConfirmsAndShowsTheRestartingBanner()
    {
        using var context = new BunitContext();
        var (_, _, rig) = Configure(context);
        rig.Setup(service => service.RequestRestartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<CameraAgentRestartDisposition>.Success(CameraAgentRestartDisposition.Scheduled));
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#control-restart").HasAttribute("disabled")));

        cut.Find("#control-restart").Click();
        Assert.AreEqual("Restart CameraAgent?", cut.Find("#control-dialog-heading").TextContent);
        Assert.IsEmpty(cut.FindAll("#control-reason"));
        cut.Find("#control-dialog-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            StringAssert.Contains(cut.Markup, "Restarting CameraAgent.", StringComparison.Ordinal);
            Assert.IsEmpty(cut.FindAll("dialog"));
            Assert.AreEqual("Restarting…", cut.Find("#control-restart").TextContent);
            Assert.IsTrue(cut.Find("#control-restart").HasAttribute("disabled"));
        });
        rig.Verify(service => service.RequestRestartAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public void Restart_ThatTheHostRefuses_StaysOpenWithTheReason()
    {
        using var context = new BunitContext();
        var (_, _, rig) = Configure(context);
        rig.Setup(service => service.RequestRestartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<CameraAgentRestartDisposition>.Success(CameraAgentRestartDisposition.Unsupervised));
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#control-restart").HasAttribute("disabled")));
        cut.Find("#control-restart").Click();

        cut.Find("#control-dialog-confirm").Click();

        cut.WaitForAssertion(() => StringAssert.StartsWith(
            cut.Find(".dialog-error").TextContent, "This host is not supervised", StringComparison.Ordinal));
        Assert.IsFalse(cut.Markup.Contains("Restarting CameraAgent.", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(false, true, "Not supervised", "This host is not supervised, so it cannot restart itself. Restart it from wherever it was started.")]
    [DataRow(true, false, "Restart", "Operations change rights are required to restart CameraAgent.")]
    public void Restart_Unavailable_IsDisabledWithTheReason(bool supervised, bool canRequest, string label, string reason)
    {
        using var context = new BunitContext();
        var (_, _, rig) = Configure(context);
        rig.Setup(service => service.GetRestartStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<CameraAgentRestartStatus>.Success(new(supervised, canRequest, false)));

        var cut = context.Render<ControlPage>();

        cut.WaitForAssertion(() =>
        {
            var trigger = cut.Find("#control-restart");
            Assert.AreEqual(label, trigger.TextContent);
            Assert.IsTrue(trigger.HasAttribute("disabled"));
            Assert.AreEqual(reason, cut.Find("#control-restart-unavailable").TextContent);
        });
    }

    [TestMethod]
    public void Restart_StatusUnreadable_IsDisabledWithTheReason()
    {
        using var context = new BunitContext();
        var (_, _, rig) = Configure(context);
        rig.Setup(service => service.GetRestartStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<CameraAgentRestartStatus>.Failure(OperatorUiResultKind.Unavailable, "Unavailable."));

        var cut = context.Render<ControlPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.IsTrue(cut.Find("#control-restart").HasAttribute("disabled"));
            Assert.AreEqual(
                "Restart availability could not be read. Refresh to try again.", cut.Find("#control-restart-unavailable").TextContent);
        });
    }

    [TestMethod]
    public void Receipts_ShowActorsReasonsAndWindowsInPlainWords()
    {
        using var context = new BunitContext();
        var (_, system, _) = Configure(context);
        var limit = 0;
        var now = OperatorUiTestData.Now;
        system.ReceiptsHandler = (requested, _) =>
        {
            limit = requested;
            return ValueTask.FromResult(OperatorUiResult<IReadOnlyList<SystemControlReceipt>>.Success(
            [
                new(now, SystemControlReceiptKind.PauseCapture, SystemControlActor.LocalOwner, CameraAgentOperatorUiService.PauseReasonCode,
                    SystemControlOutcome.Applied, null, null, null),
                new(now.AddMinutes(-5), SystemControlReceiptKind.ResumeCapture, SystemControlActor.Installer, "Upgrade finished",
                    SystemControlOutcome.NoChange, null, null, null),
                new(now.AddMinutes(-10), SystemControlReceiptKind.OverrideCreated, SystemControlActor.System,
                    "transferred from revision 5f0c2a to 9bd41e", SystemControlOutcome.Applied,
                    CaptureScheduleOverrideMode.ForceOpen, now.AddHours(1), now.AddHours(3)),
                new(now.AddMinutes(-15), SystemControlReceiptKind.OverrideConsumed, SystemControlActor.System, "capture admission",
                    SystemControlOutcome.Applied, CaptureScheduleOverrideMode.ForceClosed, null, null),
                new(now.AddMinutes(-20), SystemControlReceiptKind.OverrideCleared, SystemControlActor.LocalOwner, null,
                    SystemControlOutcome.Pending, null, null, null),
            ]));
        };

        var cut = context.Render<ControlPage>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".control-receipts tbody tr");
            Assert.HasCount(5, rows);
            Assert.AreEqual(CameraAgentSystemUiService.MaxReceipts, limit);
            AssertRow(rows[0], "23 Jul 2026 12:00:00 +00:00 (UTC (site time zone unavailable))", "Pause acquisition", "Local owner", "Maintenance (no note given)", "Completed");
            AssertRow(rows[1], "23 Jul 2026 11:55:00 +00:00 (UTC (site time zone unavailable))", "Resume acquisition", "Installer", "Upgrade finished", "Already current");
            AssertRow(rows[2], "23 Jul 2026 11:50:00 +00:00 (UTC (site time zone unavailable))", "Force open override created", "System",
                "Carried over when the camera rig changed", "Completed");
            Assert.AreEqual("23 Jul 2026 13:00:00 +00:00 (UTC (site time zone unavailable)) to 23 Jul 2026 15:00:00 +00:00 (UTC (site time zone unavailable))", rows[2].Children[1].QuerySelector("small")!.TextContent);
            Assert.IsNull(rows[3].Children[1].QuerySelector("small"));
            AssertRow(rows[3], "23 Jul 2026 11:45:00 +00:00 (UTC (site time zone unavailable))", "Force closed override used", "System", "Used by capture admission", "Completed");
            AssertRow(rows[4], "23 Jul 2026 11:40:00 +00:00 (UTC (site time zone unavailable))", "Schedule override cleared", "Local owner", "None given", "Pending");
            Assert.IsFalse(cut.Markup.Contains("5f0c2a", StringComparison.Ordinal));
            Assert.IsFalse(cut.Markup.Contains(CameraAgentOperatorUiService.PauseReasonCode, StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void Receipts_EmptyAndUnavailable_SayWhichItIs()
    {
        using var empty = new BunitContext();
        Configure(empty);
        var emptyCut = empty.Render<ControlPage>();
        emptyCut.WaitForAssertion(() => Assert.AreEqual(
            "No pause, resume or schedule override has been recorded on this agent yet.", emptyCut.Find(".control-empty").TextContent));

        using var failed = new BunitContext();
        var (_, system, _) = Configure(failed);
        system.ReceiptsHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<IReadOnlyList<SystemControlReceipt>>.Failure(
            OperatorUiResultKind.Unavailable, "Control receipts could not be read."));
        var failedCut = failed.Render<ControlPage>();
        failedCut.WaitForAssertion(() =>
        {
            StringAssert.Contains(
                failedCut.Find("section[aria-labelledby='control-receipts-heading'] [role='alert']").TextContent,
                "Control receipts could not be read.", StringComparison.Ordinal);
            Assert.IsEmpty(failedCut.FindAll(".control-receipts"));
            Assert.IsFalse(failedCut.Find("#control-capture").HasAttribute("disabled"));
        });
    }

    [TestMethod]
    public void Unauthorized_OnRefresh_ClearsTheStateAndLeaves()
    {
        using var context = new BunitContext();
        var (operations, system, _) = Configure(context);
        system.ReceiptsHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<IReadOnlyList<SystemControlReceipt>>.Success(
        [
            new(OperatorUiTestData.Now, SystemControlReceiptKind.PauseCapture, SystemControlActor.LocalOwner, "Dome maintenance",
                SystemControlOutcome.Applied, null, null, null),
        ]));
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "Dome maintenance", StringComparison.Ordinal));
        operations.OperationsHandler = _ => ValueTask.FromResult(OperatorUiResult<CameraAgentOperationsView>.Failure(
            OperatorUiResultKind.Unauthorized, "Denied."));

        cut.Find("#control-refresh").Click();

        cut.WaitForAssertion(() =>
        {
            var navigation = context.Services.GetRequiredService<NavigationManager>();
            Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
            Assert.IsFalse(cut.Markup.Contains("Dome maintenance", StringComparison.Ordinal));
            Assert.IsEmpty(cut.FindAll(".control-receipts"));
        });
    }

    [TestMethod]
    public void Unauthorized_OnConfirm_ClosesTheDialogAndLeaves()
    {
        using var context = new BunitContext();
        var (operations, _, _) = Configure(context);
        operations.CaptureHandler = (_, _, _, _) => Task.FromResult(
            OperatorUiResult<OperatorCommandReceipt>.Failure(OperatorUiResultKind.Unauthorized, "Denied."));
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() => Assert.IsFalse(cut.Find("#control-capture").HasAttribute("disabled")));
        cut.Find("#control-capture").Click();

        cut.Find("#control-dialog-confirm").Click();

        cut.WaitForAssertion(() =>
        {
            var navigation = context.Services.GetRequiredService<NavigationManager>();
            Assert.IsTrue(navigation.Uri.EndsWith("/Account/AccessDenied", StringComparison.Ordinal));
            Assert.IsEmpty(cut.FindAll("dialog"));
        });
    }

    [TestMethod]
    public void OtherControls_LinkToTheirOwnPagesOrSayWhyTheyAreUnavailable()
    {
        using var context = new BunitContext();
        Configure(context);

        var cut = context.Render<ControlPage>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("/operations/schedule?action=override", cut.Find("#control-override").GetAttribute("href"));
            Assert.AreEqual("/operations/environment?action=acquire", cut.Find("#control-acquire").GetAttribute("href"));
            StringAssert.StartsWith(cut.Find("#control-drain-unavailable").TextContent, "No drain command exists.", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find("#control-upgrade-unavailable").TextContent, "hvo-skymonitor cameraagent upgrade", StringComparison.Ordinal);
            StringAssert.Contains(cut.Find("#control-purge-unavailable").TextContent, "hvo-skymonitor cameraagent purge", StringComparison.Ordinal);
            Assert.AreEqual(3, cut.FindAll("button[disabled][aria-describedby$='-unavailable']")
                .Count(button => button.GetAttribute("aria-describedby") is "control-drain-unavailable" or "control-upgrade-unavailable" or "control-purge-unavailable"));
        });
    }

    [TestMethod]
    public void Refresh_ReadsTheClockAgainWithTheRestOfThePage()
    {
        using var context = new BunitContext();
        Configure(context);
        var time = new TestTimeSyncUiService();
        context.Services.AddSingleton<ICameraAgentTimeSyncUiService>(time);
        var cut = context.Render<ControlPage>();
        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("In tolerance", cut.Find("#time-status").TextContent);
            Assert.AreEqual(1, time.Reads);
        });

        cut.Find("#control-refresh").Click();

        cut.WaitForAssertion(() => Assert.AreEqual(2, time.Reads));
    }

    [TestMethod]
    [DataRow(null, "None given")]
    [DataRow("", "None given")]
    [DataRow("operator-maintenance", "Maintenance (no note given)")]
    [DataRow("operator-resume", "Resume (no note given)")]
    [DataRow("capture admission", "Used by capture admission")]
    [DataRow("transferred from revision a1 to b2", "Carried over when the camera rig changed")]
    [DataRow("Transferred from revision a1 to b2", "Transferred from revision a1 to b2")]
    [DataRow("Dew heater check", "Dew heater check")]
    public void ReasonText_ReadsRecordedCodesAsWords(string? reason, string expected)
        => Assert.AreEqual(expected, ControlPage.ReasonText(reason));

    private static (TestOperatorUiService Operations, TestSystemUiService System, Mock<ICameraAgentNamedRigUiService> Rig) Configure(BunitContext context)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var operations = new TestOperatorUiService();
        var system = new TestSystemUiService();
        var rig = new Mock<ICameraAgentNamedRigUiService>();
        rig.Setup(service => service.GetRestartStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperatorUiResult<CameraAgentRestartStatus>.Success(new(true, true, false)));
        context.Services.AddSingleton<ICameraAgentOperatorUiService>(operations);
        context.Services.AddSingleton<ICameraAgentSystemUiService>(system);
        context.Services.AddSingleton(rig.Object);
        context.Services.AddSingleton<ICameraAgentTimeSyncUiService>(new TestTimeSyncUiService());
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(OperatorUiTestData.Now));
        return (operations, system, rig);
    }

    private static CameraAgentOperationsView WithCapture(string state, long version)
    {
        var current = OperatorUiTestData.Operations();
        return current with
        {
            Summary = current.Summary with
            {
                CaptureControl = current.Summary.CaptureControl with
                {
                    Value = current.Summary.CaptureControl.Value with { State = state, Version = version },
                },
            },
        };
    }

    /// <summary>Compares each cell's text; the action cell is compared by its title, without the override window.</summary>
    private static void AssertRow(AngleSharp.Dom.IElement row, params string[] cells)
        => CollectionAssert.AreEqual(
            cells,
            row.Children.Select(static cell => (cell.QuerySelector("strong") ?? cell).TextContent.Trim()).ToArray());
}
