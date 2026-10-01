using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class TimeSyncPanelTests
{
    [TestMethod]
    public void Panel_ShowsTheMeasurementFromThisAgentsPointOfView()
    {
        using var context = new BunitContext();
        Configure(context);

        var cut = context.Render<TimeSyncPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("In tolerance", cut.Find("#time-status").TextContent);
            StringAssert.Contains(cut.Find("#time-status").ClassName, "success", StringComparison.Ordinal);
            Assert.AreEqual("The clock is within the 500 ms tolerance of network time.", cut.Find("#time-status-detail").TextContent);
            // The server is 12 ms behind this agent, so this agent's clock is 12 ms ahead.
            Assert.AreEqual("12 ms ahead", cut.Find("#time-offset").TextContent);
            Assert.AreEqual("pool.ntp.org, stratum 2", cut.Find("#time-server").TextContent);
            Assert.AreEqual("18 ms", cut.Find("#time-round-trip").TextContent);
            Assert.AreEqual("4m 0s ago", cut.Find("#time-measured").TextContent);
            Assert.AreEqual("2026-07-23 11:56:00 UTC", cut.Find("#time-measured").GetAttribute("title"));
            Assert.AreEqual("500 mschecked every 30 min", cut.Find("#time-tolerance").TextContent);
            Assert.AreEqual("Synchronized, ±3 ms estimated", cut.Find("#time-kernel").TextContent);
            Assert.AreEqual("2026-07-23 12:00:00", cut.Find("#time-agent-utc").TextContent);
            Assert.AreEqual("pool.ntp.org, time.example.org", cut.Find("#time-servers").GetAttribute("value"));
            Assert.IsNull(cut.Find("#time-check").GetAttribute("disabled"));
        });
    }

    [TestMethod]
    public void Panel_ListsEachServerInTheLatestCheck()
    {
        using var context = new BunitContext();
        Configure(context);

        var cut = context.Render<TimeSyncPanel>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".time-servers-table tbody tr");
            Assert.HasCount(2, rows);
            Assert.AreEqual("pool.ntp.org", rows[0].QuerySelector("strong")!.TextContent);
            Assert.AreEqual("Used for the offset", rows[0].QuerySelector("small")!.TextContent);
            string[] selected = ["Answered", "12 ms ahead", "18 ms", "2"];
            CollectionAssert.AreEqual(
                selected,
                rows[0].Children.Skip(1).Select(static cell => cell.TextContent.Trim()).ToArray());
            Assert.AreEqual("time.example.org", rows[1].QuerySelector("strong")!.TextContent);
            Assert.IsNull(rows[1].QuerySelector("small"));
            string[] failed = ["No reply in time", "—", "—", "—"];
            CollectionAssert.AreEqual(
                failed,
                rows[1].Children.Skip(1).Select(static cell => cell.TextContent.Trim()).ToArray());
        });
    }

    [TestMethod]
    public void Panel_OffersNoWayToSetTheHostClockOrUseGpsYet()
    {
        using var context = new BunitContext();
        Configure(context);

        var cut = context.Render<TimeSyncPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.IsNotNull(cut.Find("#time-set-clock").GetAttribute("disabled"));
            Assert.AreEqual(TimeSyncPanel.SetClockUnavailableReason, cut.Find("#time-set-unavailable").TextContent);
            Assert.IsNotNull(cut.Find("fieldset.time-gps").GetAttribute("disabled"));
            Assert.AreEqual(TimeSyncPanel.GpsUnavailableReason, cut.Find("#time-gps-unavailable").TextContent);
        });
    }

    [TestMethod]
    [DataRow(KernelClockStatus.Synchronized, 0.25d, "Synchronized, estimated error under 1 ms")]
    [DataRow(KernelClockStatus.Synchronized, -1d, "Synchronized")]
    [DataRow(KernelClockStatus.Unsynchronized, 16_000d, "Not synchronized")]
    [DataRow(KernelClockStatus.Unknown, -1d, "Not reported by this host")]
    public void Panel_DescribesTheHostTimeService(KernelClockStatus status, double estimatedMilliseconds, string expected)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        TimeSpan? error = estimatedMilliseconds < 0 ? null : TimeSpan.FromMilliseconds(estimatedMilliseconds);
        service.GetHandler = _ => Success(TestTimeSyncUiService.View(kernel: new KernelClockState(status, null, error)));

        var cut = context.Render<TimeSyncPanel>();

        cut.WaitForAssertion(() => Assert.AreEqual(expected, cut.Find("#time-kernel").TextContent));
    }

    [TestMethod]
    public void Panel_BeforeTheFirstCheck_SaysNothingWasMeasured()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(TestTimeSyncUiService.View(ClockSyncStatus.NotMeasured, measured: false));

        var cut = context.Render<TimeSyncPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Not measured", cut.Find("#time-status").TextContent);
            Assert.AreEqual("Not measured", cut.Find("#time-offset").TextContent);
            Assert.AreEqual("—", cut.Find("#time-server").TextContent);
            Assert.AreEqual("Not yet", cut.Find("#time-measured").TextContent);
            Assert.IsEmpty(cut.FindAll(".time-servers-table"));
        });
    }

    [TestMethod]
    public void Panel_WhenNoServerAnswered_SaysSo()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(TestTimeSyncUiService.View(ClockSyncStatus.Unverified) with { Selected = null });

        var cut = context.Render<TimeSyncPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("No server answered", cut.Find("#time-offset").TextContent);
            Assert.AreEqual("None", cut.Find("#time-server").TextContent);
            Assert.AreEqual("—", cut.Find("#time-round-trip").TextContent);
        });
    }

    [TestMethod]
    public void Panel_WhenCheckingIsOff_DisablesCheckNowAndSaysWhy()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(TestTimeSyncUiService.View(ClockSyncStatus.Disabled, measured: false));

        var cut = context.Render<TimeSyncPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Checking off", cut.Find("#time-status").TextContent);
            Assert.IsNotNull(cut.Find("#time-check").GetAttribute("disabled"));
            Assert.AreEqual(
                "Clock checking is turned off in this agent's settings.", cut.Find("#time-check-unavailable").TextContent);
        });
    }

    [TestMethod]
    public void Panel_WithoutChangeRights_IsReadOnlyAndSaysWhy()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(TestTimeSyncUiService.View(canChange: false));

        var cut = context.Render<TimeSyncPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.IsNotNull(cut.Find("#time-check").GetAttribute("disabled"));
            Assert.AreEqual(
                "Operations change rights are required to check the clock.", cut.Find("#time-check").GetAttribute("title"));
            Assert.IsNotNull(cut.Find("fieldset.time-settings-form:not(.time-gps)").GetAttribute("disabled"));
            Assert.AreEqual(
                "Operations change rights are required to change the time servers.",
                cut.Find("#time-servers-unavailable").TextContent);
        });
    }

    [TestMethod]
    [DataRow("no-file", "This host loads no operator settings file")]
    [DataRow("unreadable", "The operator settings file is not valid settings JSON.")]
    [DataRow("overridden", "Another configuration source sets the time servers")]
    public void Panel_WhenTheListCannotBeSavedHere_DisablesTheEditorAndSaysWhy(string scenario, string expected)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var setting = new TimeSyncServersSetting(["pool.ntp.org"], true, "version-1", false);
        setting = scenario switch
        {
            "no-file" => setting with { Version = null },
            "unreadable" => setting with { Unreadable = true },
            _ => setting with { Overridden = true },
        };
        service.GetHandler = _ => Success(TestTimeSyncUiService.View(setting: setting));

        var cut = context.Render<TimeSyncPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.IsNotNull(cut.Find("fieldset.time-settings-form:not(.time-gps)").GetAttribute("disabled"));
            StringAssert.StartsWith(cut.Find("#time-servers-unavailable").TextContent, expected, StringComparison.Ordinal);
            // Check now does not depend on the settings file.
            Assert.IsNull(cut.Find("#time-check").GetAttribute("disabled"));
        });
    }

    [TestMethod]
    [DataRow(1, "1 configured entry is ignored because it is not a valid server name or exceeds the limit. Saving a list here replaces them.")]
    [DataRow(3, "3 configured entries are ignored because they are not valid server names or exceed the limit. Saving a list here replaces them.")]
    public void Panel_CountsIgnoredEntriesWithoutRepeatingThem(int ignored, string expected)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(TestTimeSyncUiService.View(ignoredEntries: ignored));

        var cut = context.Render<TimeSyncPanel>();

        cut.WaitForAssertion(() => Assert.AreEqual(expected, cut.Find("#time-ignored").TextContent));
    }

    [TestMethod]
    public void Panel_WithTheDefaultList_LeavesTheInputEmptyAndDisablesUseDefault()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(TestTimeSyncUiService.View(
            setting: new TimeSyncServersSetting(TimeSyncSettings.DefaultServers, true, "version-1", false)));

        var cut = context.Render<TimeSyncPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(string.Empty, cut.Find("#time-servers").GetAttribute("value"));
            Assert.IsNotNull(cut.Find("#time-servers-default").GetAttribute("disabled"));
            StringAssert.Contains(
                cut.Find(".time-settings .time-note").TextContent,
                "None is configured, so the default pool.ntp.org is used.",
                StringComparison.Ordinal);
        });
    }

    [TestMethod]
    public void Panel_WhenEveryConfiguredEntryIsInvalid_SaysNoServerIsQueried()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => Success(TestTimeSyncUiService.View(
            setting: new TimeSyncServersSetting([], false, "version-1", false), ignoredEntries: 2));

        var cut = context.Render<TimeSyncPanel>();

        cut.WaitForAssertion(() => StringAssert.Contains(
            cut.Find(".time-settings .time-note").TextContent,
            "No configured entry is a valid server, so no server is queried.",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void Panel_WhenTheStateCannotBeRead_ShowsTheReasonAndNoControls()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.GetHandler = _ => ValueTask.FromResult(OperatorUiResult<TimeSyncView>.Failure(
            OperatorUiResultKind.Unavailable, "The clock state could not be read."));

        var cut = context.Render<TimeSyncPanel>();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(
                "Clock state unavailable. The clock state could not be read.",
                cut.Find("[role=alert]").TextContent.Trim());
            Assert.IsEmpty(cut.FindAll("#time-check"));
            Assert.IsEmpty(cut.FindAll("#time-status"));
        });
    }

    [TestMethod]
    [DataRow(ClockCheckOutcome.Measured, "Clock checked.")]
    [DataRow(ClockCheckOutcome.Joined, "A check was already running; its result is shown.")]
    [DataRow(ClockCheckOutcome.RateLimited, "The clock was checked less than 30 s ago, so that result is shown. Check again after 12:00:12 UTC.")]
    public void CheckNow_ReportsHowItWasAnsweredAndReadsTheClockAgain(ClockCheckOutcome outcome, string expected)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CheckHandler = _ => ValueTask.FromResult(OperatorUiResult<TimeSyncCheckView>.Success(new(
            outcome, outcome == ClockCheckOutcome.RateLimited ? OperatorUiTestData.Now.AddSeconds(12) : null)));
        var cut = context.Render<TimeSyncPanel>();
        cut.WaitForAssertion(() => cut.Find("#time-check"));

        cut.Find("#time-check").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(expected, cut.Find(".time-message").TextContent.Trim());
            Assert.AreEqual("status", cut.Find(".time-message").GetAttribute("role"));
            Assert.AreEqual(1, service.Checks);
            Assert.AreEqual(2, service.Reads);
        });
    }

    [TestMethod]
    public void CheckNow_WhenRefused_ShowsTheReasonAsAnAlert()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.CheckHandler = _ => ValueTask.FromResult(OperatorUiResult<TimeSyncCheckView>.Failure(
            OperatorUiResultKind.Unavailable, "The clock check could not be run."));
        var cut = context.Render<TimeSyncPanel>();
        cut.WaitForAssertion(() => cut.Find("#time-check"));

        cut.Find("#time-check").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("The clock check could not be run.", cut.Find(".time-message").TextContent.Trim());
            Assert.AreEqual("alert", cut.Find(".time-message").GetAttribute("role"));
        });
    }

    [TestMethod]
    public void CheckNow_KeepsWhatTheOperatorTypedWhileTheStoredListIsUnchanged()
    {
        using var context = new BunitContext();
        Configure(context);
        var cut = context.Render<TimeSyncPanel>();
        cut.WaitForAssertion(() => cut.Find("#time-servers"));
        cut.Find("#time-servers").Change("draft.example");

        cut.Find("#time-check").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("Clock checked.", cut.Find(".time-message").TextContent.Trim());
            Assert.AreEqual("draft.example", cut.Find("#time-servers").GetAttribute("value"));
        });
    }

    [TestMethod]
    public void SaveServers_SendsTheParsedListAtTheReadVersionAndShowsTheSavedList()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.SaveHandler = (servers, _) =>
        {
            service.GetHandler = _ => Success(TestTimeSyncUiService.View(
                setting: new TimeSyncServersSetting(servers, false, "version-2", false)));
            return ValueTask.FromResult(OperatorUiResult<TimeSyncServersSetting>.Success(new(servers, false, "version-2", false)));
        };
        var cut = context.Render<TimeSyncPanel>();
        cut.WaitForAssertion(() => cut.Find("#time-servers"));
        cut.Find("#time-servers").Change(" a.example,b.example:1123  [::1] ");

        cut.Find("#time-servers-save").Click();

        cut.WaitForAssertion(() =>
        {
            var (servers, version) = service.Saves.Single();
            string[] expected = ["a.example", "b.example:1123", "[::1]"];
            CollectionAssert.AreEqual(expected, servers.ToArray());
            Assert.AreEqual("version-1", version);
            Assert.AreEqual("Time servers saved. The next check uses them.", cut.Find(".time-message").TextContent.Trim());
            Assert.AreEqual("a.example, b.example:1123, [::1]", cut.Find("#time-servers").GetAttribute("value"));
        });
    }

    [TestMethod]
    public void UseDefault_SendsAnEmptyList()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var cut = context.Render<TimeSyncPanel>();
        cut.WaitForAssertion(() => cut.Find("#time-servers-default"));

        cut.Find("#time-servers-default").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.IsEmpty(service.Saves.Single().Servers);
            Assert.AreEqual(
                "The default time server is in use again from the next check.", cut.Find(".time-message").TextContent.Trim());
        });
    }

    [TestMethod]
    [DataRow("  ", "Enter at least one time server, or choose Use default.")]
    [DataRow("a.example, b.example, c.example, d.example, e.example", "List at most 4 time servers.")]
    [DataRow("ntp://a.example", "Each time server must be a host name or IP address")]
    [DataRow("a.example A.example", "Each time server can be listed only once.")]
    public void SaveServers_AnInvalidList_IsRefusedWithoutSending(string input, string expected)
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var cut = context.Render<TimeSyncPanel>();
        cut.WaitForAssertion(() => cut.Find("#time-servers"));
        cut.Find("#time-servers").Change(input);

        cut.Find("#time-servers-save").Click();

        cut.WaitForAssertion(() =>
        {
            StringAssert.StartsWith(cut.Find(".time-message").TextContent.Trim(), expected, StringComparison.Ordinal);
            Assert.AreEqual("alert", cut.Find(".time-message").GetAttribute("role"));
            Assert.IsEmpty(service.Saves);
            Assert.AreEqual(input, cut.Find("#time-servers").GetAttribute("value"));
        });
    }

    [TestMethod]
    public void SaveServers_WhenRefused_KeepsTheInputAndShowsWhy()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        service.SaveHandler = (_, _) => ValueTask.FromResult(OperatorUiResult<TimeSyncServersSetting>.Failure(
            OperatorUiResultKind.Conflict, "The settings file changed since this page was read. Refresh before retrying."));
        var cut = context.Render<TimeSyncPanel>();
        cut.WaitForAssertion(() => cut.Find("#time-servers"));
        cut.Find("#time-servers").Change("a.example");

        cut.Find("#time-servers-save").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual(
                "The settings file changed since this page was read. Refresh before retrying.",
                cut.Find(".time-message").TextContent.Trim());
            Assert.AreEqual("a.example", cut.Find("#time-servers").GetAttribute("value"));
            Assert.AreEqual(1, service.Reads);
        });
    }

    [TestMethod]
    public void Generation_ReadsTheClockAgainOnlyWhenItChanges()
    {
        using var context = new BunitContext();
        var service = Configure(context);
        var cut = context.Render<TimeSyncPanel>(parameters => parameters.Add(panel => panel.Generation, 0));
        cut.WaitForAssertion(() => Assert.AreEqual(1, service.Reads));

        cut.Render(parameters => parameters.Add(panel => panel.Generation, 0));
        cut.Render(parameters => parameters.Add(panel => panel.Generation, 1));

        cut.WaitForAssertion(() => Assert.AreEqual(2, service.Reads));
    }

    private static TestTimeSyncUiService Configure(BunitContext context)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var service = new TestTimeSyncUiService();
        context.Services.AddSingleton<ICameraAgentTimeSyncUiService>(service);
        context.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(OperatorUiTestData.Now));
        return service;
    }

    private static ValueTask<OperatorUiResult<TimeSyncView>> Success(TimeSyncView view)
        => ValueTask.FromResult(OperatorUiResult<TimeSyncView>.Success(view));
}
