using System.Runtime.Versioning;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Cli.Client;
using HVO.SkyMonitor.CameraAgent.Cli.Ui;
using HVO.SkyMonitor.TerminalUi;
using Terminal.Gui.Input;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli.Ui;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
[UnsupportedOSPlatform("windows")]
public sealed class HvoSkyUiFollowUpRenderTests
{
    public TestContext TestContext { get; set; } = null!;

    private static CliHarness Operator()
    {
        var harness = new CliHarness(signedIn: false);
        harness.Environment["XDG_CONFIG_HOME"] = "/home/operator/.config";
        harness.Environment["HVO_SKY_URL"] = CliHarness.Url;
        harness.Environment["HVO_SKY_API_KEY"] = CliHarness.Key;
        return harness;
    }

    [TestMethod]
    [DataRow(80, 24)]
    [DataRow(120, 32)]
    public void AgeAlone_RefreshesControlsAndLastKnownCaptions(int width, int height)
    {
        using var harness = Operator();
        using var driver = SkyUiDriver.Start(harness, width, height);
        foreach (var page in new[] { 1, 2, 3, 4 })
        {
            driver.ShowPage(page);
        }

        var restart = driver.Ui.Page.Buttons.Single(button => button.Text == "[ Restart ]");
        Assert.IsTrue(restart.Enabled);
        ((SkyUiClock)harness.Time).Now += TimeSpan.FromMinutes(1);
        harness.Api.Hang = true;

        driver.Ui.Tick();

        StringAssert.Contains(driver.Screen, "[STALE]", StringComparison.Ordinal);
        Assert.IsFalse(restart.Enabled, driver.Screen);
        StringAssert.Contains(driver.Ui.Page.Describe(), "CameraAgent / LAST KNOWN", StringComparison.Ordinal);
        driver.Render(TestContext, $"ui-{width}x{height}-5-system-aged");
        foreach (var page in new[] { 0, 1, 2, 3, 4 })
        {
            driver.Ui.ShowPage(page);
            StringAssert.Contains(driver.Ui.Page.Describe(), "LAST KNOWN", StringComparison.Ordinal);
        }
        driver.Ui.Data.Summary.Succeeded(driver.Ui.Data.Summary.Value, driver.Ui.Now);
        driver.Ui.UpdateChrome();
        Assert.IsTrue(restart.Enabled);
        Assert.DoesNotContain("LAST KNOWN", driver.Ui.Page.Describe(), StringComparison.Ordinal);
        driver.Ui.Quit();
        driver.WaitIdle("the cancelled poll");
    }

    [TestMethod]
    [DataRow(1, "Change")]
    [DataRow(2, "Stage")]
    [DataRow(2, "Apply")]
    [DataRow(3, "Activate")]
    [DataRow(4, "Restart")]
    public void AnOpenReview_RechecksLiveWriteEligibility(int page, string action)
    {
        using var harness = Operator();
        harness.Api.Json("GET", Fake.RigRoute, Fake.Rigs("rig-1", action == "Stage" ? null : "rig-2"));
        using var driver = SkyUiDriver.Start(harness, timeout: TimeSpan.FromSeconds(1));
        driver.ShowPage(page);
        if (action is "Stage" or "Activate")
        {
            driver.Press(Key.CursorDown);
        }

        driver.Ui.Page.Press(action);
        driver.WaitIdle("the review");
        var prompt = driver.Ui.Prompt!;
        if (prompt.Acknowledgement is { } acknowledge)
        {
            acknowledge.Checked = true;
        }

        ((SkyUiClock)harness.Time).Now += TimeSpan.FromMinutes(1);
        prompt.Choose();
        driver.WaitIdle("review submission");

        Assert.IsNotNull(driver.Ui.Prompt);
        StringAssert.Contains(prompt.Error, "live status", StringComparison.Ordinal);
        Assert.IsFalse(harness.Api.Requests.Any(request => request.Method is "PUT" or "POST"));
    }

    [TestMethod]
    [DataRow(80, 24)]
    [DataRow(120, 32)]
    public void ALongCatalogueReview_ScrollsAndKeepsBothActionsVisible(int width, int height)
    {
        using var harness = Operator();
        var catalogue = SkyApiClient.Read<SkySettingsCatalogue>(JsonSerializer.SerializeToElement(Fake.Catalogue(), SkyApiClient.Json));
        var schedule = catalogue.Groups.Single(group => group.Name == "schedule");
        var descriptor = schedule.Settings.Single() with
        {
            AllowedValues = [.. Enumerable.Range(1, 20).Select(number => new SkySettingChoice($"schedule-{number}", $"Schedule {number}", null))]
        };
        harness.Api.Json("GET", Fake.SettingsRoute + "/catalogue", catalogue with
        {
            Groups = [.. catalogue.Groups.Select(group => group.Name == "schedule" ? group with { Settings = [descriptor] } : group)]
        });
        using var driver = SkyUiDriver.Start(harness, width, height);
        driver.ShowPage(1);
        var field = driver.Ui.Data.Settings.Value!.FindField("schedule.activeRevision", exactOnly: true)!;

        var prompt = SkyUiSettingChange.Open(driver.Ui, field);

        _ = driver.Screen;
        Assert.IsTrue(prompt.Panel.Frame.Height <= height - 6, driver.Screen);
        StringAssert.Contains(driver.Screen, "[ Save ]", StringComparison.Ordinal);
        StringAssert.Contains(driver.Screen, "[ Cancel ]", StringComparison.Ordinal);
        StringAssert.Contains(driver.Screen, "PgUp/PgDn scroll", StringComparison.Ordinal);
        prompt.Choices!.SetFocus();
        driver.Press(Key.End);
        Assert.AreEqual(19, prompt.Choices.Value);
        StringAssert.Contains(driver.Screen, "(*) schedule-20", StringComparison.Ordinal);
        StringAssert.Contains(driver.Screen, "[ Save ]", StringComparison.Ordinal);
        StringAssert.Contains(driver.Screen, "[ Cancel ]", StringComparison.Ordinal);
        driver.Press(Key.PageUp);
        StringAssert.Contains(driver.Screen, "( ) schedule-1", StringComparison.Ordinal);
        StringAssert.Contains(driver.Screen, "[ Save ]", StringComparison.Ordinal);
        driver.Press(Key.Home);
        driver.Press(Key.End);
        prompt.Fields[0].SetFocus();
        StringAssert.Contains(driver.Screen, "Reason", StringComparison.Ordinal);
        prompt.Choices.SetFocus();
        StringAssert.Contains(driver.Screen, "(*) schedule-20", StringComparison.Ordinal);
        driver.Render(TestContext, $"ui-{width}x{height}-2-settings-long-review");
        prompt.Choose();
        driver.WaitIdle("the chosen schedule");
        var sent = harness.Api.RequestsTo("PUT", Fake.SettingsRoute + "/schedule").Single().BodyJson();
        Assert.AreEqual("schedule-20", sent.GetProperty("values").GetProperty("schedule.activeRevision").GetString());
    }

    [TestMethod]
    [DataRow(80, 24)]
    [DataRow(120, 32)]
    public void About_IsKeyboardReachableAndKeepsItsScrollAcrossRefresh(int width, int height)
    {
        using var harness = Operator();
        var catalogue = SkyApiClient.Read<SkySettingsCatalogue>(JsonSerializer.SerializeToElement(Fake.Catalogue(), SkyApiClient.Json));
        harness.Api.Json("GET", Fake.SettingsRoute + "/catalogue", catalogue with
        {
            Groups = [.. catalogue.Groups.Select(group => group.Name == "site" ? group with
            {
                Settings = [group.Settings[0] with { Description = string.Join(" ", Enumerable.Range(1, 80).Select(n => $"Description-{n}.")) }]
            } : group)]
        });
        using var driver = SkyUiDriver.Start(harness, width, height);
        driver.ShowPage(1);
        var page = (SkyUiSettingsPage)driver.Ui.Page;
        var about = page.SubViews.OfType<HvoPanel>().Single(panel => panel.Caption.StartsWith("About", StringComparison.Ordinal))
            .Body.SubViews.OfType<HvoTextLines>().Single();
        _ = driver.Screen;

        for (var tab = 0; tab < 8 && !about.HasFocus; tab++)
        {
            driver.Press(Key.Tab);
        }

        Assert.IsTrue(about.HasFocus, "Tab reaches About.");
        driver.Press(Key.PageDown);
        var top = about.Top;
        Assert.IsGreaterThan(0, top);
        driver.PressAndWait(Key.R.WithCtrl, "settings refresh");
        Assert.AreEqual(top, about.Top);
        Assert.IsTrue(about.HasFocus);
        driver.Render(TestContext, $"ui-{width}x{height}-2-settings-about-scrolled");
        page.GroupTable.SetFocus();
        driver.Press(Key.CursorDown);
        Assert.AreEqual(0, about.Top, "A different setting starts at the top.");
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"status\":\"Unexpected\"}")]
    [DataRow("{\"status\":\"Applied\"}")]
    [DataRow("{\"status\":\"Unchanged\"}")]
    [DataRow("{\"status\":\"PendingRestart\"}")]
    [DataRow("{\"status\":\"Applied\",\"group\":{\"group\":\"skyMap\",\"version\":\"v2\",\"settings\":[]},\"recorded\":[]}")]
    [DataRow("{\"status\":\"Unexpected\",\"group\":{\"group\":\"skyMap\",\"version\":\"v2\",\"settings\":[{\"key\":\"skyMap.maximumObjects\",\"value\":450}]},\"recorded\":[]}")]
    [DataRow("{\"status\":\"Applied\",\"group\":null,\"recorded\":[]}")]
    [DataRow("{\"status\":\"Applied\",\"group\":{\"group\":\"site\",\"version\":\"v2\",\"settings\":[{\"key\":\"skyMap.maximumObjects\",\"value\":450}]},\"recorded\":[]}")]
    [DataRow("{\"status\":\"Applied\",\"group\":{\"group\":\"skyMap\",\"version\":\" \",\"settings\":[{\"key\":\"skyMap.maximumObjects\",\"value\":450}]},\"recorded\":[]}")]
    [DataRow("{\"status\":\"Applied\",\"group\":{\"group\":\"skyMap\",\"version\":\"v2\",\"settings\":null},\"recorded\":[]}")]
    [DataRow("{\"status\":\"Applied\",\"group\":{\"group\":\"skyMap\",\"version\":\"v2\",\"settings\":[{\"key\":\"skyMap.maximumObjects\",\"value\":450}]}}")]
    [DataRow("{\"status\":\"Applied\",\"group\":{\"group\":\"skyMap\",\"version\":\"v2\",\"settings\":[{\"key\":\"skyMap.maximumObjects\",\"value\":450}]},\"recorded\":[null]}")]
    public void AnIncompleteSettingsReceipt_IsUnverified(string receipt)
    {
        using var harness = Operator();
        using var document = JsonDocument.Parse(receipt);
        harness.Api.Json("PUT", Fake.SettingsRoute + "/skyMap", document.RootElement.Clone());
        using var driver = SkyUiDriver.Start(harness);
        driver.ShowPage(1);
        var field = driver.Ui.Data.Settings.Value!.FindField("skyMap.maximumObjects", exactOnly: true)!;
        var prompt = SkyUiSettingChange.Open(driver.Ui, field);
        prompt.Fields[0].Text = "450";

        prompt.Choose();
        driver.WaitIdle("the receipt");

        Assert.HasCount(1, harness.Api.RequestsTo("PUT", Fake.SettingsRoute + "/skyMap"));
        StringAssert.StartsWith(driver.Ui.Notice.Message, "[UNVERIFIED]", StringComparison.Ordinal);
        Assert.DoesNotContain("was not saved", driver.Ui.Notice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("in effect at once", driver.Ui.Notice.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("Applied")]
    [DataRow("PendingRestart")]
    [DataRow("Unchanged")]
    public void ACompleteSettingsReceipt_ReportsTheKnownOutcome(string status)
    {
        using var harness = Operator();
        harness.Api.Json("PUT", Fake.SettingsRoute + "/skyMap", new
        {
            status,
            group = Fake.GroupState("skyMap", "skyMap-v2"),
            recorded = Array.Empty<string>()
        });
        using var driver = SkyUiDriver.Start(harness);
        driver.ShowPage(1);
        var prompt = SkyUiSettingChange.Open(driver.Ui, driver.Ui.Data.Settings.Value!.FindField("skyMap.maximumObjects", exactOnly: true)!);
        prompt.Fields[0].Text = "450";
        prompt.Choose();
        driver.WaitIdle("the complete receipt");
        StringAssert.StartsWith(driver.Ui.Notice.Message, "[OK]", StringComparison.Ordinal);
        StringAssert.Contains(driver.Ui.Notice.Message, status switch
        {
            "Unchanged" => "Nothing changed",
            "PendingRestart" => "next CameraAgent start",
            _ => "in effect at once"
        }, StringComparison.Ordinal);
    }

    [TestMethod]
    public void AnOpenReview_RechecksTheKeysCurrentAccess()
    {
        using var harness = Operator();
        using var driver = SkyUiDriver.Start(harness);
        driver.ShowPage(4);
        driver.Ui.Page.Press("Restart");
        var info = driver.Ui.Data.Info.Value!;
        driver.Ui.Data.Info.Succeeded(info with { Caller = info.Caller with { Access = "Read" } }, driver.Ui.Now);
        driver.Ui.Prompt!.Choose();
        Assert.IsNotNull(driver.Ui.Prompt);
        StringAssert.Contains(driver.Ui.Prompt.Error, "read-only", StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.RequestsTo("POST", Fake.RestartRoute));
    }
}
