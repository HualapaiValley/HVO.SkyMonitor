using HVO.SkyMonitor.CameraAgent.Settings;
using HVO.SkyMonitor.Common.Configuration;
using Microsoft.Extensions.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Tests.Settings;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSettingSourcesTests
{
    [TestMethod]
    public void Find_NamesTheTopmostLayerAndWhetherItOutranksTheSettingsFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-setting-sources", Guid.NewGuid().ToString("N"));
        var secrets = Path.Combine(root, "secrets");
        Directory.CreateDirectory(secrets);
        var prefix = $"HVO_SOURCES_{Guid.NewGuid():N}_";
        try
        {
            File.WriteAllText(Path.Combine(root, "appsettings.json"),
                """{ "Probe": { "Image": "1", "File": "1", "Environment": "1", "Secret": "1", "Line": "1" } }""");
            var settingsPath = Path.Combine(root, DeploymentLocalSettings.FileName);
            File.WriteAllText(settingsPath,
                """{ "Probe": { "File": "2", "Environment": "2", "Secret": "2", "Line": "2" } }""");
            File.WriteAllText(Path.Combine(secrets, "Probe__Secret"), "3");
            Environment.SetEnvironmentVariable(prefix + "Probe__Environment", "3");
            var configuration = new ConfigurationManager();
            configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [DeploymentLocalSettings.PathVariable] = settingsPath });
            configuration.AddJsonFile(Path.Combine(root, "appsettings.json"));
            var local = DeploymentLocalSettings.AddConfiguredFile(configuration, root)!;
            configuration.AddEnvironmentVariables(prefix);
            configuration.AddKeyPerFile(secrets, optional: false);
            configuration.AddCommandLine(["--Probe:Line=3"]);
            var sources = new CameraAgentSettingSources(configuration, local.IsSettingsFileProvider);

            Assert.AreEqual(new CameraAgentConfigurationSource(CameraAgentSettingSource.AppSettings, false),
                sources.Find("Probe:Image"));
            Assert.AreEqual(new CameraAgentConfigurationSource(CameraAgentSettingSource.SettingsFile, false),
                sources.Find("Probe:File"));
            Assert.AreEqual(new CameraAgentConfigurationSource(CameraAgentSettingSource.Environment, true),
                sources.Find("Probe:Environment"));
            Assert.AreEqual(new CameraAgentConfigurationSource(CameraAgentSettingSource.SecretsDirectory, true),
                sources.Find("Probe:Secret"));
            Assert.AreEqual(new CameraAgentConfigurationSource(CameraAgentSettingSource.CommandLine, true),
                sources.Find("Probe:Line"));
            Assert.AreEqual(new CameraAgentConfigurationSource(CameraAgentSettingSource.Default, false),
                sources.Find("Probe:Missing"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "Probe__Environment", null);
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Find_WithoutASettingsFile_NeverReportsAnOverride()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Probe:Value"] = "1" });
        var sources = new CameraAgentSettingSources(configuration, static _ => false);

        Assert.AreEqual(new CameraAgentConfigurationSource(CameraAgentSettingSource.Host, false), sources.Find("Probe:Value"));
    }
}
