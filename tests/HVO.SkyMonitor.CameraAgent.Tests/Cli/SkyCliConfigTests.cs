using System.Net;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli;

/// <summary><c>config show|get|set|diff|apply</c>, built from the settings catalogue (#1316), against the fake API.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SkyCliConfigTests
{
    private static readonly string[] UnchangedSite = ["site.observatoryName"];
    private static readonly string[] ChangedSkyMapAndCamera = ["skyMap.maximumObjects", "camera.skyLighting"];

    [TestMethod]
    public async Task Show_ListsEveryGroupFromTheCatalogueWithValuesAndSourcesAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("config", "show").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        foreach (var expected in new[]
        {
            "[site] Site profile", "[location] Deployment location", "[skyMap] Sky map", "[camera] Camera", "[schedule] Capture schedule",
            "site.observatoryName", "Hualapai Valley", "SettingsFile", "skyMap.maximumObjects", "400", "applies after a restart"
        })
        {
            StringAssert.Contains(result.Out, expected, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task Show_OneGroup_AndAReadOnlyGroupSaysWhyAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Fake.SettingsRoute, Fake.Values(siteWritable: false));

        var result = await harness.RunAsync("config", "show", "SITE").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "[site] Site profile (read-only: The site group is set by an environment variable.)", StringComparison.Ordinal);
        Assert.DoesNotContain("[skyMap]", result.Out, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Show_UnknownGroup_IsAUsageErrorListingTheGroupsAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("config", "show", "network").ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "Groups: site, location, skyMap, camera, schedule.", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Show_Json_CarriesTheCatalogueAndTheStateOfEachSettingAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("config", "show", "--json").ConfigureAwait(false);

        var groups = result.Json().GetProperty("groups");
        Assert.AreEqual(5, groups.GetArrayLength());
        var skyMap = groups[2];
        Assert.AreEqual("skyMap-v1", skyMap.GetProperty("version").GetString());
        var limit = skyMap.GetProperty("settings")[0];
        Assert.AreEqual("skyMap.maximumObjects", limit.GetProperty("key").GetString());
        Assert.AreEqual(400, limit.GetProperty("value").GetInt32());
        Assert.AreEqual(50, limit.GetProperty("minimum").GetDouble());
        Assert.AreEqual("CaptureOutput", limit.GetProperty("safety").GetString());
        Assert.IsTrue(limit.GetProperty("canWrite").GetBoolean());
    }

    [TestMethod]
    [DataRow("skyMap.maximumObjects")]
    [DataRow("maximumObjects")]
    [DataRow("SKYMAP.MAXIMUMOBJECTS")]
    public async Task Get_FindsASettingByItsKeyOrItsUniqueShortNameAsync(string key)
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("config", "get", key).ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "Scene object limit (skyMap.maximumObjects)", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "Accepts:", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "50 to 2000", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "Default: 400", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "changes what future captures record", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Get_UnknownSetting_IsAUsageErrorAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("config", "get", "site.colour").ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "There is no setting 'site.colour'", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Set_SendsTheGroupWithTheVersionReadTheReasonAndAnIdempotencyKeyAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("config", "set", "skyMap.maximumObjects=500", "--reason", "fewer labels").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        var put = harness.Api.RequestsTo("PUT", Fake.SettingsRoute + "/skyMap").Single();
        var body = put.BodyJson();
        Assert.AreEqual("skyMap-v1", body.GetProperty("expectedVersion").GetString());
        Assert.AreEqual(500, body.GetProperty("values").GetProperty("skyMap.maximumObjects").GetInt32());
        Assert.AreEqual("fewer labels", body.GetProperty("reason").GetString());
        Assert.IsFalse(string.IsNullOrEmpty(put.IdempotencyKey));
        StringAssert.Contains(result.Out, "Scene object limit (skyMap.maximumObjects): 400 -> 500", StringComparison.Ordinal);
        StringAssert.Contains(result.Out, "[skyMap] Saved; in effect now.", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Set_ARestartSetting_SaysItTakesEffectAtTheNextStartAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("config", "set", "location.latitudeDegrees=35.25", "--force").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "takes effect at the next CameraAgent start ('hvo-sky restart')", StringComparison.Ordinal);
        Assert.AreEqual(35.25, harness.Api.RequestsTo("PUT", Fake.SettingsRoute + "/location").Single().BodyJson()
            .GetProperty("values").GetProperty("location.latitudeDegrees").GetDouble());
    }

    [TestMethod]
    [DataRow("skyMap.maximumObjects=49", "skyMap.maximumObjects takes 50 to 2000.")]
    [DataRow("skyMap.maximumObjects=many", "skyMap.maximumObjects takes a whole number.")]
    [DataRow("skyMap.maximumObjects=", "skyMap.maximumObjects may not be empty.")]
    [DataRow("location.latitudeDegrees=91", "location.latitudeDegrees takes -90 to 90 degrees.")]
    [DataRow("location.latitudeDegrees=NaN", "location.latitudeDegrees takes a number")]
    [DataRow("camera.skyLighting=Dusk", "camera.skyLighting takes one of: SolarDriven, ControlledNight.")]
    [DataRow("site.observatoryName", "is not key=value")]
    public async Task Set_AValueTheCatalogueRefuses_IsAUsageErrorAndNothingIsSentAsync(string assignment, string expected)
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("config", "set", assignment, "--force").ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, expected, StringComparison.Ordinal);
        Assert.IsEmpty(harness.Api.RequestsTo("PUT", Fake.SettingsRoute + "/skyMap"));
        Assert.IsFalse(harness.Api.Requests.Any(static request => request.Method == "PUT"));
    }

    [TestMethod]
    public async Task Set_TooLongText_IsRefusedWithoutRepeatingItAsync()
    {
        using var harness = new CliHarness();
        var text = new string('x', 65);

        var result = await harness.RunAsync("config", "set", $"site.observatoryName={text}").ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "takes at most 64 characters", StringComparison.Ordinal);
        Assert.DoesNotContain(text, result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Set_AnEnumIsMatchedIgnoringCaseAndSentAsCataloguedAndAnEmptyValueClearsANullableSettingAsync()
    {
        using var harness = new CliHarness();

        var lighting = await harness.RunAsync("config", "set", "camera.skyLighting=controllednight").ConfigureAwait(false);
        var cleared = await harness.RunAsync("config", "set", "site.observatoryName=").ConfigureAwait(false);

        Assert.AreEqual(0, lighting.ExitCode, lighting.ToString());
        Assert.AreEqual("ControlledNight", harness.Api.RequestsTo("PUT", Fake.SettingsRoute + "/camera").Single().BodyJson()
            .GetProperty("values").GetProperty("camera.skyLighting").GetString());
        Assert.AreEqual(0, cleared.ExitCode, cleared.ToString());
        Assert.AreEqual(JsonValueKind.Null, harness.Api.RequestsTo("PUT", Fake.SettingsRoute + "/site").Single().BodyJson()
            .GetProperty("values").GetProperty("site.observatoryName").ValueKind);
    }

    [TestMethod]
    public async Task Set_TwoGroupsAtOnce_IsAUsageErrorAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("config", "set", "skyMap.maximumObjects=500", "site.observatoryName=HVO").ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "Change one group at a time. These settings are in: skyMap, site.", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Set_TheSameValue_SendsNothingAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("config", "set", "skyMap.maximumObjects=400").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "Nothing to change", StringComparison.Ordinal);
        Assert.IsFalse(harness.Api.Requests.Any(static request => request.Method == "PUT"));
    }

    [TestMethod]
    public async Task Set_AReadOnlySetting_IsRefusedWithItsReasonBeforeAnythingIsSentAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Fake.SettingsRoute, Fake.Values(siteWritable: false));

        var result = await harness.RunAsync("config", "set", "site.observatoryName=HVO").ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "site.observatoryName cannot be changed here: Set by an environment variable. Nothing was sent.", StringComparison.Ordinal);
        Assert.IsFalse(harness.Api.Requests.Any(static request => request.Method == "PUT"));
    }

    [TestMethod]
    public async Task Set_DryRun_ShowsTheChangeAndSendsNothingAsync()
    {
        using var harness = new CliHarness();

        var text = await harness.RunAsync("config", "set", "skyMap.maximumObjects=500", "--dry-run").ConfigureAwait(false);
        var json = await harness.RunAsync("config", "set", "skyMap.maximumObjects=500", "--dry-run", "--json").ConfigureAwait(false);

        Assert.AreEqual(0, text.ExitCode, text.ToString());
        StringAssert.Contains(text.Out, "Not sent (--dry-run).", StringComparison.Ordinal);
        Assert.IsTrue(json.Json().GetProperty("dryRun").GetBoolean());
        Assert.AreEqual(500, json.Json().GetProperty("changes")[0].GetProperty("to").GetInt32());
        Assert.IsFalse(harness.Api.Requests.Any(static request => request.Method == "PUT"));
    }

    [TestMethod]
    public async Task Set_TheLocation_IsConfirmedAtATerminalAndNeedsForceWithoutOneAsync()
    {
        using var harness = new CliHarness();

        var scripted = await harness.RunAsync("config", "set", "location.latitudeDegrees=35.25").ConfigureAwait(false);
        harness.Interactive = true;
        harness.Input.Enqueue("n");
        var declined = await harness.RunAsync("config", "set", "location.latitudeDegrees=35.25").ConfigureAwait(false);
        harness.Input.Enqueue("yes");
        var confirmed = await harness.RunAsync("config", "set", "location.latitudeDegrees=35.25").ConfigureAwait(false);

        Assert.AreEqual(2, scripted.ExitCode, scripted.ToString());
        StringAssert.Contains(scripted.Error, "confirm it with --force", StringComparison.Ordinal);
        Assert.AreEqual(3, declined.ExitCode, declined.ToString());
        Assert.AreEqual(0, confirmed.ExitCode, confirmed.ToString());
        Assert.HasCount(1, harness.Api.RequestsTo("PUT", Fake.SettingsRoute + "/location"));
    }

    [TestMethod]
    public async Task Set_CameraAgentsRefusals_ExitThreeWithTheirReasonsAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Problem("PUT", Fake.SettingsRoute + "/skyMap", HttpStatusCode.Conflict,
            "The settings group changed since it was read.", "settings.conflict");
        harness.Api.Problem("PUT", Fake.SettingsRoute + "/site", HttpStatusCode.BadRequest, "One or more settings are invalid.",
            "settings.invalid", new { errors = new Dictionary<string, string[]> { ["site.observatoryName"] = ["Use printable characters."] } });

        var conflict = await harness.RunAsync("config", "set", "skyMap.maximumObjects=500").ConfigureAwait(false);
        var invalid = await harness.RunAsync("config", "set", "site.observatoryName=HVO", "--json").ConfigureAwait(false);

        Assert.AreEqual(3, conflict.ExitCode, conflict.ToString());
        StringAssert.Contains(conflict.Error, "The settings group changed since it was read.", StringComparison.Ordinal);
        Assert.AreEqual(3, invalid.ExitCode, invalid.ToString());
        var error = invalid.Json().GetProperty("error");
        Assert.AreEqual("settings.invalid", error.GetProperty("code").GetString());
        Assert.AreEqual(400, error.GetProperty("status").GetInt32());
        Assert.AreEqual("Use printable characters.", error.GetProperty("errors").GetProperty("site.observatoryName")[0].GetString());
    }

    [TestMethod]
    public async Task Set_AReasonLongerThanCameraAgentRecords_IsAUsageErrorAsync()
    {
        using var harness = new CliHarness();

        var result = await harness.RunAsync("config", "set", "skyMap.maximumObjects=500", "--reason", new string('r', 513)).ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        Assert.IsEmpty(harness.Api.Requests);
    }

    [TestMethod]
    public async Task Diff_ShowsWhatAFileWouldChangeAndSendsNothingAsync()
    {
        using var harness = new CliHarness();
        var file = harness.WriteFile("s.json", """
            {"schemaVersion": 1, "settings": {"skyMap.maximumObjects": 500, "site.observatoryName": "Hualapai Valley", "camera.skyLighting": "ControlledNight"}}
            """);

        var text = await harness.RunAsync("config", "diff", file).ConfigureAwait(false);
        var json = await harness.RunAsync("config", "diff", file, "--json").ConfigureAwait(false);

        Assert.AreEqual(0, text.ExitCode, text.ToString());
        StringAssert.Contains(text.Out, "Scene object limit (skyMap.maximumObjects): 400 -> 500", StringComparison.Ordinal);
        StringAssert.Contains(text.Out, "Sky lighting (camera.skyLighting): SolarDriven -> ControlledNight  [applies at the next CameraAgent start]", StringComparison.Ordinal);
        StringAssert.Contains(text.Out, "2 to change, 1 already as in the file.", StringComparison.Ordinal);
        CollectionAssert.AreEqual(UnchangedSite,
            json.Json().GetProperty("unchanged").EnumerateArray().Select(static item => item.GetString()).ToArray());
        CollectionAssert.AreEqual(ChangedSkyMapAndCamera,
            json.Json().GetProperty("changes").EnumerateArray().Select(static item => item.GetProperty("key").GetString()).ToArray());
        Assert.IsFalse(harness.Api.Requests.Any(static request => request.Method == "PUT"));
    }

    [TestMethod]
    public async Task Diff_ReadsStandardInputAndSaysWhenThereIsNoDifferenceAsync()
    {
        using var harness = new CliHarness { StandardInput = """{"schemaVersion": 1, "settings": {"skyMap.maximumObjects": 400}}""" };

        var result = await harness.RunAsync("config", "diff", "-").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "No differences", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("""{"schemaVersion": 1, "settings": {}, "extra": 1}""", "unknown member 'extra'")]
    [DataRow("""{"schemaVersion": 2, "settings": {}}""", "\"schemaVersion\": 1")]
    [DataRow("""{"schemaVersion": "1", "settings": {}}""", "\"schemaVersion\": 1")]
    [DataRow("""{"schemaVersion": true, "settings": {}}""", "\"schemaVersion\": 1")]
    [DataRow("""{"schemaVersion": null, "settings": {}}""", "\"schemaVersion\": 1")]
    [DataRow("""{"schemaVersion": 1.5, "settings": {}}""", "\"schemaVersion\": 1")]
    [DataRow("""{"schemaVersion": 1}""", "\"settings\" object")]
    [DataRow("""{"schemaVersion": 1, "settings": {"maximumObjects": 500}}""", "There is no setting 'maximumObjects'")]
    [DataRow("""{"schemaVersion": 1, "settings": {"skyMap.maximumobjects": 500}}""", "the setting is skyMap.maximumObjects, as written")]
    [DataRow("""{"schemaVersion": 1, "settings": {"skyMap.maximumObjects": "500"}}""", "skyMap.maximumObjects takes a whole number")]
    [DataRow("""{"schemaVersion": 1, "settings": {"skyMap.maximumObjects": null}}""", "may not be cleared")]
    [DataRow("""{"schemaVersion": 1, "settings": {"skyMap.maximumObjects": 1, "skyMap.maximumObjects": 2}}""", "gives skyMap.maximumObjects twice")]
    [DataRow("[1]", "must be a JSON object")]
    [DataRow("{", "not valid JSON")]
    public async Task Diff_AFileThatCannotBeUsed_IsAUsageErrorAsync(string content, string expected)
    {
        using var harness = new CliHarness();
        var file = harness.WriteFile("s.json", content);

        var result = await harness.RunAsync("config", "diff", file).ConfigureAwait(false);

        Assert.AreEqual(2, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, expected, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Apply_SendsEachGroupInCatalogueOrderWithItsOwnVersionAsync()
    {
        using var harness = new CliHarness();
        var file = harness.WriteFile("s.json", """
            {"schemaVersion": 1, "settings": {"camera.skyLighting": "ControlledNight", "skyMap.maximumObjects": 500, "site.observatoryName": "HVO"}}
            """);

        var result = await harness.RunAsync("config", "apply", file, "--json").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        CollectionAssert.AreEqual(
            new[] { Fake.SettingsRoute + "/site", Fake.SettingsRoute + "/skyMap", Fake.SettingsRoute + "/camera" },
            harness.Api.Requests.Where(static request => request.Method == "PUT").Select(static request => request.PathAndQuery).ToArray());
        Assert.AreEqual("camera-v1", harness.Api.RequestsTo("PUT", Fake.SettingsRoute + "/camera").Single().BodyJson()
            .GetProperty("expectedVersion").GetString());
        Assert.AreEqual(3, result.Json().GetProperty("groups").GetArrayLength());
    }

    [TestMethod]
    public async Task Apply_AReadOnlySettingAnywhereInTheFile_RefusesBeforeAnyGroupIsSentAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Json("GET", Fake.SettingsRoute, Fake.Values(siteWritable: false));
        var file = harness.WriteFile("s.json", """{"schemaVersion": 1, "settings": {"skyMap.maximumObjects": 500, "site.observatoryName": "HVO"}}""");

        var result = await harness.RunAsync("config", "apply", file).ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        Assert.IsFalse(harness.Api.Requests.Any(static request => request.Method == "PUT"));
    }

    [TestMethod]
    public async Task Apply_AFailureAfterAGroupWasSaved_ExitsOneAndSaysToRunItAgainAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Problem("PUT", Fake.SettingsRoute + "/skyMap", HttpStatusCode.Conflict, "The settings group changed since it was read.", "settings.conflict");
        var file = harness.WriteFile("s.json", """{"schemaVersion": 1, "settings": {"skyMap.maximumObjects": 500, "site.observatoryName": "HVO"}}""");

        var result = await harness.RunAsync("config", "apply", file).ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "[site] Saved; in effect now.", StringComparison.Ordinal);
        StringAssert.Contains(result.Error, "The skyMap group was not saved: The settings group changed since it was read. The groups before it were saved; run the command again to continue.", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Apply_TheFirstGroupRefused_ExitsThreeBecauseNothingChangedAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Problem("PUT", Fake.SettingsRoute + "/site", HttpStatusCode.Conflict, "The settings group changed since it was read.", "settings.conflict");
        var file = harness.WriteFile("s.json", """{"schemaVersion": 1, "settings": {"skyMap.maximumObjects": 500, "site.observatoryName": "HVO"}}""");

        var result = await harness.RunAsync("config", "apply", file).ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        Assert.HasCount(1, harness.Api.Requests.Where(static request => request.Method == "PUT").ToArray());
    }

    [TestMethod]
    public async Task Apply_AnUnchangedGroupThenARefusal_ExitsThreeBecauseNothingChangedAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Handle("PUT", Fake.SettingsRoute + "/site", _ => FakeCameraAgent.Response(HttpStatusCode.OK, new
        {
            status = "Unchanged",
            group = Fake.GroupState("site", version: "site-v1"),
            recorded = Array.Empty<string>()
        }));
        harness.Api.Problem("PUT", Fake.SettingsRoute + "/skyMap", HttpStatusCode.Conflict, "The settings group changed since it was read.", "settings.conflict");
        var file = harness.WriteFile("s.json", """{"schemaVersion": 1, "settings": {"skyMap.maximumObjects": 500, "site.observatoryName": "HVO"}}""");

        var result = await harness.RunAsync("config", "apply", file).ConfigureAwait(false);

        Assert.AreEqual(3, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "The settings group changed since it was read.", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Apply_InterruptedAfterAGroupWasSaved_Exits130AndSaysItWasAcceptedAsync()
    {
        using var harness = new CliHarness();
        using var stop = new CancellationTokenSource();
        harness.Api.OnRequest = request =>
        {
            if (request.Method == "PUT" && request.PathAndQuery == Fake.SettingsRoute + "/skyMap")
            {
                harness.Api.Hang = true;
                stop.Cancel();
            }
        };
        var file = harness.WriteFile("s.json", """{"schemaVersion": 1, "settings": {"skyMap.maximumObjects": 500, "site.observatoryName": "HVO"}}""");

        var result = await harness.RunAsync(stop.Token, "config", "apply", file).ConfigureAwait(false);

        Assert.AreEqual(130, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "after CameraAgent accepted the site settings", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Apply_AKeyRefusedAfterAGroupWasSaved_ExitsOneAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Problem("PUT", Fake.SettingsRoute + "/skyMap", HttpStatusCode.Unauthorized, "Unauthorized");
        var file = harness.WriteFile("s.json", """{"schemaVersion": 1, "settings": {"skyMap.maximumObjects": 500, "site.observatoryName": "HVO"}}""");

        var result = await harness.RunAsync("config", "apply", file, "--json").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        Assert.AreEqual(1, result.Json().GetProperty("error").GetProperty("exitCode").GetInt32());
    }

    [TestMethod]
    [DataRow("config set", "malformed")]
    [DataRow("config set", "stalled")]
    [DataRow("config apply", "malformed")]
    [DataRow("config apply", "stalled")]
    public async Task ASettingsChangeWhoseSuccessStatusArrivedButNotItsBody_IsAcceptedButUnverifiedAsync(string command, string body)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(body);
        using var harness = new CliHarness();
        harness.Api.Handle("PUT", Fake.SettingsRoute + "/skyMap", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = body == "stalled" ? new StalledContent() : new StringContent("{\"status\": ", System.Text.Encoding.UTF8, "application/json")
        });
        string[] args = command == "config set"
            ? ["config", "set", "skyMap.maximumObjects=500"]
            : ["config", "apply", harness.WriteFile("s.json", """{"schemaVersion": 1, "settings": {"skyMap.maximumObjects": 500}}""")];

        var result = await harness.RunAsync([.. args, "--timeout", "1", "--json"]).ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        var error = result.Json().GetProperty("error");
        Assert.AreEqual("Unverified", error.GetProperty("kind").GetString());
        Assert.AreEqual(body == "stalled" ? "TimedOut" : "Failed", error.GetProperty("cause").GetString());
        var message = error.GetProperty("message").GetString()!;
        StringAssert.StartsWith(message, "CameraAgent accepted the skyMap settings, but it could not be verified: ", StringComparison.Ordinal);
        Assert.DoesNotContain("was not saved", message, StringComparison.Ordinal);
        Assert.DoesNotContain("is not known", message, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Apply_ALaterGroupWhoseSuccessStatusArrivedButNotItsBody_IsThatGroupAcceptedButUnverifiedAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Handle("PUT", Fake.SettingsRoute + "/skyMap", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"status\": ", System.Text.Encoding.UTF8, "application/json")
        });
        var file = harness.WriteFile("s.json", """{"schemaVersion": 1, "settings": {"skyMap.maximumObjects": 500, "site.observatoryName": "HVO"}}""");

        var result = await harness.RunAsync("config", "apply", file).ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "[site] Saved; in effect now.", StringComparison.Ordinal);
        StringAssert.StartsWith(result.Error, "CameraAgent accepted the skyMap settings, but it could not be verified: ", StringComparison.Ordinal);
        Assert.DoesNotContain("was not saved", result.Error, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Apply_NoDifference_SaysNothingToChangeAsync()
    {
        using var harness = new CliHarness();
        var file = harness.WriteFile("s.json", """{"schemaVersion": 1, "settings": {"skyMap.maximumObjects": 400}}""");

        var result = await harness.RunAsync("config", "apply", file).ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Out, "Nothing to change", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ASettingsSchemaThisClientDoesNotRead_ExitsOneAsync()
    {
        using var harness = new CliHarness();
        harness.Api.Handle("GET", Fake.SettingsRoute + "/catalogue", _ =>
        {
            var catalogue = JsonSerializer.SerializeToNode(Fake.Catalogue())!;
            catalogue["schemaVersion"] = 2;
            return FakeCameraAgent.Response(HttpStatusCode.OK, catalogue);
        });

        var result = await harness.RunAsync("config", "show").ConfigureAwait(false);

        Assert.AreEqual(1, result.ExitCode, result.ToString());
        StringAssert.Contains(result.Error, "settings schema 2", StringComparison.Ordinal);
    }
}
