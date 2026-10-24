using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

namespace HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;

[TestClass]
[TestCategory("Unit")]
public sealed class SpaceWeatherAutomationAdapterTests
{
    [TestMethod]
    [DataRow(59, false)]
    [DataRow(60, true)]
    [DataRow(120, true)]
    [DataRow(121, false)]
    public async Task TickBoundsValidateEvenWhenDisabled(int interval, bool valid)
    {
        using var host = new SpaceWeatherTestHost(false); await host.InitializeAsync().ConfigureAwait(false);
        var definition = Definition() with { TriggerInterval = interval };
        Assert.AreEqual(valid, host.Adapter.Validate(definition) is null);
        Assert.IsTrue(host.Adapter.SupportsRunNow); Assert.IsTrue(host.Adapter.RunsOffSweep);
        Assert.AreEqual(SpaceWeatherTestHost.Start,
            ((ILocalAutomationPeriodicTaskAdapter)host.Adapter).ResolveInitialTriggerEpoch(SpaceWeatherTestHost.Start));
        string[] targets = ["swpc"];
        CollectionAssert.AreEqual(targets, host.Adapter.Describe().Targets.ToArray());
        CollectionAssert.AreEqual(new[] { LocalAutomationTriggerKind.Periodic }, host.Adapter.Describe().CompatibleTriggers.ToArray());
    }

    [TestMethod]
    [DataRow("id", "space-weather.reserved-definition-id")]
    [DataRow("target", "automation.unregisteredTarget")]
    [DataRow("trigger", "automation.unregisteredCombination")]
    [DataRow("window", "automation.unregisteredCombination")]
    public void ReservedShapeIsClosed(string invalid, string reason)
    {
        using var host = new SpaceWeatherTestHost();
        var definition = invalid switch
        {
            "id" => Definition() with { DefinitionId = "other" },
            "target" => Definition() with { TaskTarget = "other" },
            "trigger" => Definition() with { TriggerKind = LocalAutomationTriggerKind.CaptureRelative },
            _ => Definition() with
            {
                SourceWindow = new(LocalAutomationSourceWindowPolicy.CurrentVersion,
                LocalAutomationSourceWindowKind.SunriseDay, LocalAutomationSourceSelection.AllActualSources, TimeSpan.FromMinutes(5))
            }
        };
        Assert.AreEqual(reason, host.Adapter.Validate(definition)?.ReasonCode);
    }

    [TestMethod]
    [DataRow("space-weather-refresh|0123456789abcdef|n1", SwpcAttemptTrigger.Manual)]
    [DataRow("space-weather-refresh|0123456789abcdef|p1", SwpcAttemptTrigger.Scheduled)]
    public async Task RunNowUsesTheManualTrigger(string key, SwpcAttemptTrigger trigger)
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false);
        var definition = Definition();
        await host.Adapter.ExecuteAsync(LocalAutomationOccurrence.Create(new(definition, 1,
            LocalAutomationContract.ComputeRevisionSha256(definition), null, null), key, host.Clock.GetUtcNow()), default).ConfigureAwait(false);
        Assert.IsTrue((await host.Store.ReadDiagnosticsAsync(new(host.Clock.GetUtcNow(), null, null, 10), default).ConfigureAwait(false))
            .Attempts.All(attempt => attempt.Trigger == trigger));
    }
    private static LocalAutomationDefinition Definition() => new("space-weather-refresh", "SWPC", true,
        LocalAutomationTaskKind.SpaceWeatherRefresh, "swpc", LocalAutomationTriggerKind.Periodic, 120, SpaceWeatherTestHost.Start);
}
