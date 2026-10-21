using HVO.SkyMonitor.CameraAgent.Settings;
using HVO.SkyMonitor.Common.Configuration;
using Microsoft.Extensions.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Tests.Settings;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentFeatureSettingsTests
{
    [TestMethod]
    [DataRow("orbitalElements", true)]
    [DataRow("satellitePlanning", false)]
    [DataRow("spaceWeather", true)]
    public async Task Switches_SaveBooleanToTheOperatorFileAndReportApplicationAsync(string group, bool restart)
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var before = await harness.GroupAsync(group).ConfigureAwait(false);
        Assert.AreEqual("false", before.Settings.Single().Value!.Value.GetString());
        var result = await harness.WriteAsync(group, before.Version,
            new Dictionary<string, object?> { [group + ".enabled"] = "true" }).ConfigureAwait(false);
        Assert.AreEqual(restart ? CameraAgentSettingsWriteOutcome.PendingRestart : CameraAgentSettingsWriteOutcome.Applied, result.Outcome);
        Assert.AreEqual(restart ? "false" : "true", result.Group!.Settings.Single().Value!.Value.GetString());
        Assert.AreEqual(restart ? "true" : null, result.Group.Settings.Single().PendingValue?.GetString());
        StringAssert.Contains(harness.ReadSettingsFile(), "\"Enabled\": true", StringComparison.Ordinal);
        Assert.IsTrue(bool.Parse(harness.Configuration[CameraAgentSettingsCatalogue.Find(group + ".enabled")!.ConfigurationKey!]!));
        var off = await harness.WriteAsync(group, result.Group.Version,
            new Dictionary<string, object?> { [group + ".enabled"] = "false" }).ConfigureAwait(false);
        Assert.AreEqual("false", off.Group!.Settings.Single().Value!.Value.GetString());
        Assert.IsNull(off.Group.Settings.Single().PendingValue);
        StringAssert.Contains(harness.ReadSettingsFile(), "\"Enabled\": false", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task OrbitalOff_LeavesTheRunningOptionOnUntilRestartAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync(orbitalInitiallyEnabled: true).ConfigureAwait(false);
        var group = await harness.GroupAsync("orbitalElements").ConfigureAwait(false);
        var result = await harness.WriteAsync(group.Group, group.Version,
            new Dictionary<string, object?> { ["orbitalElements.enabled"] = "false" }).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.PendingRestart, result.Outcome);
        Assert.AreEqual("true", result.Group!.Settings.Single().Value!.Value.GetString());
        Assert.AreEqual("false", result.Group.Settings.Single().PendingValue!.Value.GetString());
    }

    [TestMethod]
    public async Task SpaceWeatherOff_ReportsItsOwnRunningOptionUntilRestartAsync()
    {
        // Orbital refresh starts off and space weather on, so the effective value can only come from SpaceWeather.
        using var harness = await CameraAgentSettingsHarness.CreateAsync(
            orbitalInitiallyEnabled: false, spaceWeatherInitiallyEnabled: true).ConfigureAwait(false);
        var orbital = await harness.GroupAsync("orbitalElements").ConfigureAwait(false);
        Assert.AreEqual("false", orbital.Settings.Single().Value!.Value.GetString());
        var group = await harness.GroupAsync("spaceWeather").ConfigureAwait(false);
        var before = group.Settings.Single();
        Assert.AreEqual("true", before.Value!.Value.GetString());
        Assert.AreEqual("false", before.PendingValue!.Value.GetString(), "The settings file still says off, so the running value is pending a restart.");

        var result = await harness.WriteAsync(group.Group, group.Version,
            new Dictionary<string, object?> { ["spaceWeather.enabled"] = "false" }).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentSettingsWriteOutcome.PendingRestart, result.Outcome);
        Assert.AreEqual("true", result.Group!.Settings.Single().Value!.Value.GetString());
        Assert.AreEqual("false", result.Group.Settings.Single().PendingValue!.Value.GetString());
        Assert.AreEqual("false", (await harness.GroupAsync("orbitalElements").ConfigureAwait(false)).Settings.Single().Value!.Value.GetString());
    }

    [TestMethod]
    [DataRow("orbitalElements", "Environment")]
    [DataRow("satellitePlanning", "Environment")]
    [DataRow("orbitalElements", "SecretsDirectory")]
    [DataRow("satellitePlanning", "SecretsDirectory")]
    [DataRow("spaceWeather", "Environment")]
    [DataRow("spaceWeather", "SecretsDirectory")]
    public async Task Switches_EnvironmentAndSecretsOverrideTheFileAndRefuseWritesAsync(string group, string provider)
    {
        var key = CameraAgentSettingsCatalogue.Find(group + ".enabled")!.ConfigurationKey!;
        var root = Path.Combine(Path.GetTempPath(), "hvo-feature-override", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var variable = "HVO_FEATURE_" + Guid.NewGuid().ToString("N") + "_";
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, key.Replace(":", "__", StringComparison.Ordinal)), "true").ConfigureAwait(false);
            Environment.SetEnvironmentVariable(variable + key.Replace(":", "__", StringComparison.Ordinal), "true");
            var fileContents = "{\"CameraAgent\":{\"" + key.Split(':')[1] + "\":{\"Enabled\":false}}}";
            using var harness = await CameraAgentSettingsHarness.CreateAsync(configureOverrides: configuration =>
            {
                File.WriteAllText(configuration[DeploymentLocalSettings.PathVariable]!, fileContents);
                ((IConfigurationRoot)configuration).Reload();
                if (provider == "Environment") configuration.AddEnvironmentVariables(variable);
                else configuration.AddKeyPerFile(root, optional: false);
            }, orbitalInitiallyEnabled: true, spaceWeatherInitiallyEnabled: true).ConfigureAwait(false);
            var before = await harness.GroupAsync(group).ConfigureAwait(false);
            var setting = before.Settings.Single();
            Assert.AreEqual(provider, setting.Source.ToString());
            Assert.AreEqual("true", setting.Value!.Value.GetString());
            Assert.IsFalse(setting.CanWrite);
            Assert.IsNotNull(setting.ReadOnlyReason);
            var result = await harness.WriteAsync(group, before.Version,
                new Dictionary<string, object?> { [setting.Key] = "false" }).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentSettingsWriteOutcome.ReadOnly, result.Outcome);
            Assert.AreEqual(fileContents, harness.ReadSettingsFile());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable + key.Replace(":", "__", StringComparison.Ordinal), null);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Switches_RejectUnknownChoiceAndStaleVersionWithoutOverwritingOtherSettingsAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        var before = await harness.GroupAsync("orbitalElements").ConfigureAwait(false);
        var invalid = await harness.WriteAsync(before.Group, before.Version,
            new Dictionary<string, object?> { ["orbitalElements.enabled"] = "sometimes" }).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Invalid, invalid.Outcome);
        await File.WriteAllTextAsync(harness.SettingsPath, """{"Keep":"unchanged"}""").ConfigureAwait(false);
        var stale = await harness.WriteAsync(before.Group, before.Version,
            new Dictionary<string, object?> { ["orbitalElements.enabled"] = "true" }).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentSettingsWriteOutcome.Conflict, stale.Outcome);
        Assert.AreEqual("""{"Keep":"unchanged"}""", harness.ReadSettingsFile());
    }
}
