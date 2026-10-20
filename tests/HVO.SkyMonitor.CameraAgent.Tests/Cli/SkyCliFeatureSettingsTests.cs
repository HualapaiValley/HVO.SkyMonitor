using System.Net;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Settings;
using HVO.SkyMonitor.CameraAgent.Tests.Settings;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli;

[TestClass]
[TestCategory("Unit")]
public sealed class SkyCliFeatureSettingsTests
{
    [TestMethod]
    [DataRow("orbitalElements", true, false)]
    [DataRow("satellitePlanning", false, false)]
    [DataRow("orbitalElements", true, true)]
    [DataRow("satellitePlanning", false, true)]
    public async Task Config_UsesTheRealCatalogueAndSettingsStoreForBothSwitchesAsync(string group, bool restart, bool apply)
    {
        using var settings = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        using var cli = new CliHarness();
        cli.Api.Json("GET", Fake.SettingsRoute + "/catalogue", CameraAgentSettingsCatalogue.ToResponse());
        cli.Api.Handle("GET", Fake.SettingsRoute, _ => FakeCameraAgent.Response(HttpStatusCode.OK,
            settings.Service.GetAsync(CancellationToken.None).GetAwaiter().GetResult()));
        cli.Api.Handle("PUT", Fake.SettingsRoute + "/" + group, request =>
        {
            var json = request.BodyJson();
            var result = settings.Service.WriteAsync(group, new(json.GetProperty("expectedVersion").GetString(),
                json.GetProperty("values").EnumerateObject().ToDictionary(property => property.Name, property => property.Value)),
                "owner", request.IdempotencyKey, CancellationToken.None).GetAwaiter().GetResult();
            Assert.IsTrue(result.Outcome is CameraAgentSettingsWriteOutcome.Applied or CameraAgentSettingsWriteOutcome.PendingRestart);
            return FakeCameraAgent.Response(HttpStatusCode.OK, new CameraAgentSettingsWriteResponse(restart
                ? CameraAgentSettingsWriteStatus.PendingRestart : CameraAgentSettingsWriteStatus.Applied, result.Group!, []));
        });
        var show = await cli.RunAsync("config", "show", group).ConfigureAwait(false);
        Assert.AreEqual(0, show.ExitCode, show.ToString());
        Assert.Contains(group + ".enabled", show.Out, StringComparison.Ordinal);
        var on = await cli.RunAsync("config", "set", group + ".enabled=true").ConfigureAwait(false);
        Assert.AreEqual(0, on.ExitCode, on.ToString());
        Assert.Contains(restart ? "next CameraAgent start" : "in effect now", on.Out, StringComparison.Ordinal);
        StringAssert.Contains(settings.ReadSettingsFile(), "\"Enabled\": true", StringComparison.Ordinal);
        var file = cli.WriteFile("features.json", "{\"schemaVersion\":1,\"settings\":{\"" + group + ".enabled\":\"false\"}}");
        var off = apply ? await cli.RunAsync("config", "apply", file).ConfigureAwait(false)
            : await cli.RunAsync("config", "set", group + ".enabled=false").ConfigureAwait(false);
        Assert.AreEqual(0, off.ExitCode, off.ToString());
        StringAssert.Contains(settings.ReadSettingsFile(), "\"Enabled\": false", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("orbitalElements", "Environment")]
    [DataRow("satellitePlanning", "SecretsDirectory")]
    public async Task Config_ReportsAnOverrideAndDoesNotSendAWriteAsync(string group, string source)
    {
        using var cli = new CliHarness();
        cli.Api.Json("GET", Fake.SettingsRoute + "/catalogue", CameraAgentSettingsCatalogue.ToResponse());
        var state = new CameraAgentSettingsGroupState(group, "v1", false, "A higher-precedence source overrides the settings file.",
            [new(group + ".enabled", JsonSerializer.SerializeToElement("true"), true, Enum.Parse<CameraAgentSettingSource>(source),
                null, false, "A higher-precedence source overrides the settings file.", null, null, null)]);
        cli.Api.Json("GET", Fake.SettingsRoute, new CameraAgentSettingsResponse(1, [state]));
        var show = await cli.RunAsync("config", "show", group).ConfigureAwait(false);
        Assert.AreEqual(0, show.ExitCode, show.ToString());
        Assert.Contains(source, show.Out, StringComparison.Ordinal);
        var write = await cli.RunAsync("config", "set", group + ".enabled=false").ConfigureAwait(false);
        Assert.AreNotEqual(0, write.ExitCode);
        Assert.IsEmpty(cli.Api.RequestsTo("PUT", Fake.SettingsRoute + "/" + group));
    }
}
