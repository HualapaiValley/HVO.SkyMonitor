using System.Net;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Settings;
using HVO.SkyMonitor.CameraAgent.Tests.Settings;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli;

[TestClass]
[TestCategory("Unit")]
public sealed class SkyCliScheduleSettingsTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Config_ActivatesASavedScheduleDraftThroughTheRealStoreAsync(bool apply)
    {
        using var settings = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var staged = await settings.StageScheduleDraftAsync("cli-draft", 2).ConfigureAwait(false);
        var draft = staged.PendingRevision!.RevisionId;
        var before = await settings.GroupAsync("schedule").ConfigureAwait(false);
        Assert.AreEqual(staged.ActiveRevision.RevisionId, before.Settings.Single().Value!.Value.GetString());
        Assert.AreEqual(draft, before.Settings.Single().PendingValue!.Value.GetString());
        var counts = await settings.ScheduleMutationCountsAsync().ConfigureAwait(false);
        using var cli = CreateCli(settings);
        var file = WriteDraftFile(cli, draft);

        var result = apply ? await cli.RunAsync("config", "apply", file).ConfigureAwait(false)
            : await cli.RunAsync("config", "set", "schedule.activeRevision=" + draft).ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        var writes = cli.Api.RequestsTo("PUT", Fake.SettingsRoute + "/schedule");
        Assert.HasCount(1, writes, result.ToString());
        var request = writes.Single();
        Assert.AreEqual(before.Version, request.BodyJson().GetProperty("expectedVersion").GetString());
        Assert.AreEqual(draft, request.BodyJson().GetProperty("values").GetProperty("schedule.activeRevision").GetString());
        Assert.IsFalse(string.IsNullOrEmpty(request.IdempotencyKey));
        var after = await settings.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(draft, after.ActiveRevision.RevisionId);
        var appliedCounts = await settings.ScheduleMutationCountsAsync().ConfigureAwait(false);
        Assert.AreEqual(counts.Commands + 1, appliedCounts.Commands);
        Assert.AreEqual(counts.Activations + 1, appliedCounts.Activations);
    }

    [TestMethod]
    public async Task ConfigDiff_ShowsTheActivationFromActiveRevisionToSavedDraftAsync()
    {
        using var settings = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var staged = await settings.StageScheduleDraftAsync("cli-draft", 2).ConfigureAwait(false);
        var draft = staged.PendingRevision!.RevisionId;
        using var cli = CreateCli(settings);

        var result = await cli.RunAsync("config", "diff", WriteDraftFile(cli, draft), "--json").ConfigureAwait(false);

        Assert.AreEqual(0, result.ExitCode, result.ToString());
        using var json = JsonDocument.Parse(result.Out);
        var changes = json.RootElement.GetProperty("changes").EnumerateArray().ToArray();
        Assert.HasCount(1, changes);
        Assert.AreEqual("schedule.activeRevision", changes.Single().GetProperty("key").GetString());
        Assert.AreEqual(staged.ActiveRevision.RevisionId, changes.Single().GetProperty("from").GetString());
        Assert.AreEqual(draft, changes.Single().GetProperty("to").GetString());
        Assert.IsEmpty(cli.Api.RequestsTo("PUT", Fake.SettingsRoute + "/schedule"));
        Assert.AreEqual(staged.ActiveRevision.RevisionId,
            (await settings.Schedule.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevision.RevisionId);
    }

    private static string WriteDraftFile(CliHarness cli, string draft)
        => cli.WriteFile("schedule.json", JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            settings = new Dictionary<string, string> { ["schedule.activeRevision"] = draft }
        }));

    private static CliHarness CreateCli(CameraAgentSettingsHarness settings)
    {
        var cli = new CliHarness();
        cli.Api.Json("GET", Fake.SettingsRoute + "/catalogue", CameraAgentSettingsCatalogue.ToResponse());
        cli.Api.Handle("GET", Fake.SettingsRoute, _ => FakeCameraAgent.Response(HttpStatusCode.OK,
            settings.Service.GetAsync(CancellationToken.None).GetAwaiter().GetResult()));
        cli.Api.Handle("PUT", Fake.SettingsRoute + "/schedule", request =>
        {
            var json = request.BodyJson();
            var result = settings.Service.WriteAsync("schedule", new(json.GetProperty("expectedVersion").GetString(),
                json.GetProperty("values").EnumerateObject().ToDictionary(property => property.Name, property => property.Value)),
                CameraAgentSettingsHarness.Actor, request.IdempotencyKey, CancellationToken.None).GetAwaiter().GetResult();
            Assert.AreEqual(CameraAgentSettingsWriteOutcome.Applied, result.Outcome, result.Message);
            return FakeCameraAgent.Response(HttpStatusCode.OK,
                new CameraAgentSettingsWriteResponse(CameraAgentSettingsWriteStatus.Applied, result.Group!, []));
        });
        return cli;
    }
}
