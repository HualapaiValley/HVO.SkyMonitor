using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [TestMethod]
    [DataRow("group", "canWrite")]
    [DataRow("group", "readOnlyReason")]
    [DataRow("setting", "key")]
    [DataRow("setting", "value")]
    [DataRow("setting", "isSet")]
    [DataRow("setting", "source")]
    [DataRow("setting", "sourceDetail")]
    [DataRow("setting", "canWrite")]
    [DataRow("setting", "readOnlyReason")]
    [DataRow("setting", "pendingValue")]
    [DataRow("setting", "problem")]
    [DataRow("setting", "allowedValues")]
    [DataRow("choice", "value")]
    [DataRow("choice", "label")]
    [DataRow("choice", "description")]
    public void MissingNestedReceiptMember_KeepsAcceptedWriteUnverified(string scope, string member)
    {
        var group = scope == "choice" ? "schedule" : "skyMap";
        var receipt = Receipt(group);
        var state = receipt["group"]!["settings"]![0]!;
        var target = scope switch
        {
            "group" => receipt["group"]!.AsObject(),
            "choice" => state["allowedValues"]![0]!.AsObject(),
            _ => state.AsObject()
        };
        Assert.IsTrue(target.Remove(member));
        AssertUnverifiedReceipt(receipt, group);
    }

    [TestMethod]
    [DataRow("integer-object", "Applied")]
    [DataRow("integer-string", "Applied")]
    [DataRow("integer-fraction", "Applied")]
    [DataRow("integer-boolean", "Applied")]
    [DataRow("number-object", "Applied")]
    [DataRow("string-object", "Applied")]
    [DataRow("enum-object", "Applied")]
    [DataRow("null-setting", "Applied")]
    [DataRow("key-only", "Applied")]
    [DataRow("key-only", "Unchanged")]
    [DataRow("null-choice", "Applied")]
    [DataRow("other-setting-value", "Applied")]
    [DataRow("pending-object", "Applied")]
    [DataRow("source-null", "Applied")]
    [DataRow("choice-label-type", "Applied")]
    [DataRow("sourceDetail-type", "Applied")]
    public void MalformedNestedReceipt_KeepsAcceptedWriteUnverified(string defect, string status)
    {
        var group = defect switch
        {
            "number-object" or "other-setting-value" => "location",
            "string-object" => "site",
            "enum-object" => "camera",
            "null-choice" or "choice-label-type" => "schedule",
            _ => "skyMap"
        };
        var receipt = Receipt(group);
        receipt["status"] = status;
        var settings = receipt["group"]!["settings"]!.AsArray();
        var state = settings[0]!;
        switch (defect)
        {
            case "null-setting": settings.Add((JsonNode?)null); break;
            case "key-only": settings[0] = new JsonObject { ["key"] = state["key"]!.GetValue<string>() }; break;
            case "null-choice": state["allowedValues"]!.AsArray().Add((JsonNode?)null); break;
            case "other-setting-value": settings[1]!["value"] = new JsonObject(); break;
            case "pending-object": state["pendingValue"] = new JsonObject(); break;
            case "source-null": state["source"] = null; break;
            case "choice-label-type": state["allowedValues"]![0]!["label"] = 7; break;
            case "sourceDetail-type": state["sourceDetail"] = 7; break;
            default:
                state["value"] = defect switch
                {
                    "integer-string" => JsonValue.Create("450"),
                    "integer-fraction" => JsonValue.Create(450.5),
                    "integer-boolean" => JsonValue.Create(true),
                    _ => new JsonObject()
                };
                break;
        }

        AssertUnverifiedReceipt(receipt, group);
    }

    private static JsonObject Receipt(string group) => JsonSerializer.SerializeToNode(new
    {
        status = "Applied",
        group = Fake.GroupState(group, $"{group}-v2"),
        recorded = Array.Empty<string>()
    }, SkyApiClient.Json)!.AsObject();

    private static void AssertUnverifiedReceipt(JsonObject receipt, string group)
    {
        using var harness = Operator();
        harness.Api.Json("PUT", Fake.SettingsRoute + "/" + group, receipt);
        using var driver = SkyUiDriver.Start(harness);
        driver.ShowPage(1);
        var field = driver.Ui.Data.Settings.Value!.FindGroup(group)!.Fields[0];
        var prompt = SkyUiSettingChange.Open(driver.Ui, field);
        if (prompt.Choices is null)
        {
            prompt.Fields[0].Text = group switch { "location" => "36", "site" => "Updated name", _ => "450" };
        }
        if (prompt.Acknowledgement is { } acknowledgement)
        {
            acknowledgement.Checked = true;
        }

        prompt.Choose();
        driver.WaitIdle("the nested receipt");

        Assert.HasCount(1, harness.Api.RequestsTo("PUT", Fake.SettingsRoute + "/" + group));
        // UNVERIFIED is emitted by SayOutcome only while the operation still tracks a possibly accepted write.
        StringAssert.StartsWith(driver.Ui.Notice.Message, "[UNVERIFIED]", StringComparison.Ordinal);
        Assert.DoesNotContain("was not saved", driver.Ui.Notice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing changed", driver.Ui.Notice.Message, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(1, "Change", "group-read-only")]
    [DataRow(1, "Change", "setting-read-only")]
    [DataRow(2, "Stage", "rig-staged")]
    [DataRow(2, "Apply", "rig-unstaged")]
    [DataRow(2, "Change camera", "camera-read-only")]
    [DataRow(3, "Apply staged", "schedule-unstaged")]
    [DataRow(3, "Activate", "schedule-active")]
    [DataRow(4, "Restart", "restart-requested")]
    public void AnOpenReview_RechecksItsCurrentActionEligibility(int page, string action, string change)
    {
        using var harness = Operator();
        harness.Api.Json("GET", Fake.RigRoute, Fake.Rigs("rig-1", action == "Stage" ? null : "rig-2"));
        harness.Api.Json("GET", Fake.ScheduleRoute, Fake.Schedule(action == "Apply staged" ? "schedule-4" : null));
        using var driver = SkyUiDriver.Start(harness, timeout: TimeSpan.FromSeconds(1));
        driver.ShowPage(page);
        if (action is "Stage" or "Activate")
        {
            driver.Press(Key.CursorDown);
        }
        driver.Ui.Page.Press(action);
        driver.WaitIdle("the action review");
        var prompt = driver.Ui.Prompt!;
        Assert.IsNotNull(prompt);
        if (prompt.Acknowledgement is { } acknowledgement)
        {
            acknowledgement.Checked = true;
        }
        switch (change)
        {
            case "group-read-only":
            case "setting-read-only":
            case "camera-read-only":
                var values = JsonSerializer.SerializeToNode(Fake.Values(), SkyApiClient.Json)!;
                var group = values["groups"]!.AsArray().Single(item => item!["group"]!.GetValue<string>() == (page == 2 ? "camera" : "site"))!;
                if (change == "group-read-only") group["canWrite"] = false;
                else group["settings"]![0]!["canWrite"] = false;
                harness.Api.Json("GET", Fake.SettingsRoute, values);
                break;
            case "rig-staged": harness.Api.Json("GET", Fake.RigRoute, Fake.Rigs("rig-1", "rig-2")); break;
            case "rig-unstaged": harness.Api.Json("GET", Fake.RigRoute, Fake.Rigs("rig-1", null)); break;
            case "schedule-unstaged": harness.Api.Json("GET", Fake.ScheduleRoute, Fake.Schedule()); break;
            case "schedule-active":
                var schedule = JsonSerializer.SerializeToNode(Fake.Schedule(), SkyApiClient.Json)!;
                schedule["activeRevision"] = JsonSerializer.SerializeToNode(Fake.ScheduleRevision("schedule-2", 2), SkyApiClient.Json);
                harness.Api.Json("GET", Fake.ScheduleRoute, schedule);
                break;
            case "restart-requested": harness.Api.Json("GET", Fake.InfoRoute, Fake.Info(SkyUiClock.Observed, requested: true)); break;
        }
        driver.Ui.Page.Refresh();
        driver.WaitIdle("updated action eligibility");
        Assert.AreEqual(SkyUiFreshness.Live, driver.Ui.Freshness);
        Assert.IsFalse(driver.Ui.Page.Buttons.Single(button => button.Text == $"[ {action} ]").Enabled);

        prompt.Choose();
        driver.WaitIdle("refused review submission");

        Assert.IsFalse(harness.Api.Requests.Any(request => request.Method is "PUT" or "POST"), "A review must send nothing after its action becomes unavailable.");
        Assert.AreSame(prompt, driver.Ui.Prompt);
        StringAssert.Contains(prompt.Error, "no longer available", StringComparison.Ordinal);
    }


    [TestMethod]
    [DataRow(1, "Change", "\"site-v1\"")]
    [DataRow(2, "Stage", "7")]
    [DataRow(3, "Activate", "11")]
    public void AnEligibleOpenReview_KeepsTheReviewedVersion(int page, string action, string expectedVersion)
    {
        using var harness = Operator();
        using var driver = SkyUiDriver.Start(harness);
        driver.ShowPage(page);
        if (action is "Stage" or "Activate")
        {
            driver.Press(Key.CursorDown);
        }
        driver.Ui.Page.Press(action);
        driver.WaitIdle("the versioned review");
        var prompt = driver.Ui.Prompt!;
        if (prompt.Acknowledgement is { } acknowledgement)
        {
            acknowledgement.Checked = true;
        }
        switch (page)
        {
            case 1:
                prompt.Fields[0].Text = "Updated site name";
                var values = JsonSerializer.SerializeToNode(Fake.Values(), SkyApiClient.Json)!;
                foreach (var group in values["groups"]!.AsArray()) group!["version"] = "new-version";
                harness.Api.Json("GET", Fake.SettingsRoute, values);
                break;
            case 2:
                var rigs = JsonSerializer.SerializeToNode(Fake.Rigs("rig-1", null), SkyApiClient.Json)!;
                rigs["selection"]!["version"] = 99;
                harness.Api.Json("GET", Fake.RigRoute, rigs);
                break;
            case 3:
                var schedule = JsonSerializer.SerializeToNode(Fake.Schedule(), SkyApiClient.Json)!;
                schedule["stateVersion"] = 99;
                harness.Api.Json("GET", Fake.ScheduleRoute, schedule);
                break;
        }
        driver.Ui.Page.Refresh();
        driver.WaitIdle("the newer eligible state");
        Assert.IsTrue(driver.Ui.Page.Buttons.Single(button => button.Text == $"[ {action} ]").Enabled);
        prompt.Choose();
        driver.WaitIdle("the write with its reviewed version");

        var writes = harness.Api.Requests.Where(request => request.Method is "PUT" or "POST").ToArray();
        Assert.HasCount(1, writes);
        Assert.AreEqual(expectedVersion, writes[0].BodyJson().GetProperty("expectedVersion").GetRawText());
    }

}
