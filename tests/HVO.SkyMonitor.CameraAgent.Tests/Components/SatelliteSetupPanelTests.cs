using System.Text.Json;
using Bunit;
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

    private sealed class Service(string reason, string? overridden = null) : ICameraAgentSatelliteSetupUiService
    {
        private static readonly string[] FeatureGroups = ["orbitalElements", "satellitePlanning"];
        internal List<(string, string, string)> Writes { get; } = [];
        public Task<OperatorUiResult<SatelliteSetupView>> GetAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, CancellationToken cancellationToken)
        {
            var groups = FeatureGroups.Select(group => new CameraAgentSettingsGroupState(
                group, "v1", overridden is null, overridden is null ? null : "An override controls this switch.",
                [new(group + ".enabled", JsonSerializer.SerializeToElement("false"), true,
                    overridden is null ? CameraAgentSettingSource.SettingsFile : Enum.Parse<CameraAgentSettingSource>(overridden),
                    null, overridden is null, overridden is null ? null : "This source overrides the settings file.", null, null, null)])).ToArray();
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
