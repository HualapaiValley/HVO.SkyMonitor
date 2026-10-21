using System.Text.Json;
using Bunit;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class SatelliteSetupPanelTests
{
    [TestMethod]
    [DataRow("disabled")]
    [DataRow("no-snapshot")]
    [DataRow("no-time-data")]
    [DataRow("hash-mismatch")]
    [DataRow("coverage-expired")]
    public void Status_ShowsEveryReasonWithAnActionableLinkAndReadOnlyTimeConfiguration(string reason)
    {
        using var context = new BunitContext();
        var service = new Service(reason);
        context.Services.AddSingleton<ICameraAgentSatelliteSetupUiService>(service);
        var cut = context.Render<SatelliteSetupPanel>(parameters => parameters.Add(panel => panel.AtUtc, TestOrbitalUiService.Now));
        cut.WaitForElement("#satellite-availability");
        Assert.AreEqual("/operations/site#satellite-time-data", cut.Find($"[data-reason='{reason}'] a").GetAttribute("href"));
        Assert.Contains("synthetic.finals2000A", cut.Find("#satellite-time-data").TextContent, StringComparison.Ordinal);
        Assert.Contains("Hash mismatch", cut.Find("#satellite-time-data").TextContent, StringComparison.Ordinal);
        Assert.IsEmpty(cut.FindAll("#satellite-time-data input"));
    }

    [TestMethod]
    public void Status_ShowsTheActiveSourceFinalAndPredictedCoverageAndProvenance()
    {
        using var context = new BunitContext();
        var final = new OrbitalUtcInterval(new(1973, 1, 2, 0, 0, 0, TimeSpan.Zero), new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        var predicted = new OrbitalUtcInterval(new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero), new(2027, 10, 27, 0, 0, 0, TimeSpan.Zero));
        var service = new Service("ready", view: new([], [],
            [new("Earth orientation", "/app/time-data/finals2000A.daily.extended", new('A', 64), "Bundled and hash verified",
                "Retrieved 2026-10-21T18:00:00Z from https://maia.usno.navy.mil/ser7/finals2000A.daily.extended.")],
            new(final.StartUtc, predicted.EndUtc), new(2027, 6, 28, 0, 0, 0, TimeSpan.Zero),
            "Bundled with this CameraAgent release", final, predicted, OrbitalEarthOrientationStatus.Predicted));
        context.Services.AddSingleton<ICameraAgentSatelliteSetupUiService>(service);
        var cut = context.Render<SatelliteSetupPanel>(parameters => parameters.Add(panel => panel.AtUtc, TestOrbitalUiService.Now));
        var text = cut.WaitForElement("#satellite-time-data").TextContent;
        foreach (var expected in new[] { "Bundled with this CameraAgent release", "Provenance: Retrieved 2026-10-21T18:00:00Z",
            $"{final.StartUtc:O} to {final.EndUtc:O}", $"{predicted.StartUtc:O} to {predicted.EndUtc:O}", "Predicted Earth orientation",
            "2027-06-28T00:00:00.0000000+00:00", "downloads a newer pair at most once a day" })
            Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Switches_UseGroupVersionsAndShowRestartPendingAndLiveApplication()
    {
        using var context = new BunitContext();
        var service = new Service("disabled");
        context.Services.AddSingleton<ICameraAgentSatelliteSetupUiService>(service);
        var cut = context.Render<SatelliteSetupPanel>(parameters => parameters.Add(panel => panel.AtUtc, TestOrbitalUiService.Now));
        cut.Find("#orbitalElements-enabled").Change("true");
        Assert.AreEqual(("orbitalElements", "v1", "true"), service.Writes.Single());
        Assert.Contains("applies after a CameraAgent restart", cut.Markup, StringComparison.Ordinal);
        cut.Find("#satellitePlanning-enabled").Change("true");
        Assert.AreEqual(("satellitePlanning", "v1", "true"), service.Writes.Last());
        Assert.Contains("Saved; in effect now", cut.Markup, StringComparison.Ordinal);
        cut.Find("#timeDataRefresh-enabled").Change("true");
        Assert.AreEqual(("timeDataRefresh", "v1", "true"), service.Writes.Last());
        Assert.Contains("Saved; in effect now", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Applies immediately.", cut.Find("#timeDataRefresh-switch").TextContent, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(false, "failed")]
    [DataRow(true, "adopted")]
    [DataRow(false, "never")]
    public void Refresh_ShowsTheLastAttemptAndTheAdoptionSeparatelyFromExpiry(bool expired, string outcome)
    {
        using var context = new BunitContext();
        var end = new DateTimeOffset(2027, 6, 28, 0, 0, 0, TimeSpan.Zero);
        var attempt = new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.Zero);
        var refresh = outcome == "never"
            ? new CameraAgentTimeDataRefreshStatus("never", null, null, null, null, null, null)
            : new CameraAgentTimeDataRefreshStatus(outcome, attempt, attempt.AddSeconds(4),
                outcome == "failed" ? "time-data.checksum-mismatch" : "time-data.adopted", "detail text", attempt.AddSeconds(4), attempt.AddDays(1));
        var pair = outcome == "adopted"
            ? new CameraAgentManagedTimePairStatus("20261022T060004Z-0a1b2c3d", attempt.AddSeconds(4), attempt, "verified", end, new('A', 64), new('B', 64))
            : null;
        var status = new CameraAgentTimeDataStatus(CameraAgentTimeDataStatus.CurrentVersion, attempt, true, true, outcome != "never",
            outcome == "adopted" ? "managed" : "bundled", "none", end, end.AddMonths(4), end, expired, new('A', 64), new('B', 64), refresh, pair);
        var service = new Service("ready", view: new([], [], [], null, null, TimeData: status));
        context.Services.AddSingleton<ICameraAgentSatelliteSetupUiService>(service);
        var cut = context.Render<SatelliteSetupPanel>(parameters => parameters.Add(panel => panel.AtUtc, TestOrbitalUiService.Now));
        var facts = cut.WaitForElement("#time-data-refresh");
        Assert.AreEqual(outcome, facts.GetAttribute("data-refresh-outcome"));
        Assert.AreEqual(expired ? "true" : "false", facts.GetAttribute("data-expired"));
        var text = facts.TextContent;
        Assert.Contains(expired ? "Expired: the active managed pair covered planning until" : "pair covers planning until", text, StringComparison.Ordinal);
        Assert.Contains(outcome switch
        {
            "failed" => "Failed at 2026-10-22T06:00:04.0000000+00:00 (time-data.checksum-mismatch); the active pair is unchanged",
            "adopted" => "adopted at 2026-10-22T06:00:04.0000000+00:00 (time-data.adopted)",
            _ => "No refresh has been attempted"
        }, text, StringComparison.Ordinal);
        Assert.Contains(pair is null ? "No refreshed pair has been adopted" : "2026-10-22T06:00:04.0000000+00:00 (retrieved", text, StringComparison.Ordinal);
        Assert.AreEqual(outcome == "never", cut.FindAll("#time-data-refresh a[href='/operations/automations?setup=time-data']").Count == 1,
            "the automation link is offered only while the daily automation is missing");
    }

    [TestMethod]
    [DataRow("Environment")]
    [DataRow("SecretsDirectory")]
    public void Switches_ShowHigherPrecedenceAndCannotBeEdited(string source)
    {
        using var context = new BunitContext();
        var service = new Service("disabled", source);
        context.Services.AddSingleton<ICameraAgentSatelliteSetupUiService>(service);
        var cut = context.Render<SatelliteSetupPanel>(parameters => parameters.Add(panel => panel.AtUtc, TestOrbitalUiService.Now));
        foreach (var select in cut.FindAll("select")) Assert.IsTrue(select.HasAttribute("disabled"));
        Assert.Contains(source, cut.Markup, StringComparison.Ordinal);
        Assert.Contains("overrides the settings file", cut.Markup, StringComparison.Ordinal);
        Assert.IsEmpty(service.Writes);
    }

    private sealed class Service(string reason, string? overridden = null, SatelliteSetupView? view = null) : ICameraAgentSatelliteSetupUiService
    {
        private static readonly string[] FeatureGroups = ["orbitalElements", "satellitePlanning", "timeDataRefresh"];
        internal List<(string, string, string)> Writes { get; } = [];
        public Task<OperatorUiResult<SatelliteSetupView>> GetAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken)
        {
            var groups = FeatureGroups.Select(group => new CameraAgentSettingsGroupState(
                group, "v1", overridden is null, overridden is null ? null : "An override controls this switch.",
                [new(group + ".enabled", JsonSerializer.SerializeToElement("false"), true,
                    overridden is null ? CameraAgentSettingSource.SettingsFile : Enum.Parse<CameraAgentSettingSource>(overridden),
                    null, overridden is null, overridden is null ? null : "This source overrides the settings file.", null, null, null)])).ToArray();
            if (view is not null) return Task.FromResult(OperatorUiResult<SatelliteSetupView>.Success(view with { Groups = groups }));
            return Task.FromResult(OperatorUiResult<SatelliteSetupView>.Success(new(groups,
                [new(reason, "Reason: " + reason, "/operations/site#satellite-time-data", "Fix time inputs")],
                [new("Earth orientation", "/synthetic/synthetic.finals2000A", new('a', 64), "Hash mismatch")], null, null)));
        }

        public Task<OperatorUiResult<CameraAgentSettingsWriteResult>> SetAsync(string group, string version, string value, CancellationToken cancellationToken)
        {
            Writes.Add((group, version, value));
            return Task.FromResult(OperatorUiResult<CameraAgentSettingsWriteResult>.Success(new(
                group == "orbitalElements" ? CameraAgentSettingsWriteOutcome.PendingRestart : CameraAgentSettingsWriteOutcome.Applied, null)));
        }
    }
}
