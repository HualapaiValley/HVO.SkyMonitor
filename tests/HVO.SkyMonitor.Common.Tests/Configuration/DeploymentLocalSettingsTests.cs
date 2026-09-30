using HVO.SkyMonitor.Common.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace HVO.SkyMonitor.Tests.Configuration;

/// <summary>
/// Covers where the operator's settings file comes from, where it sits among the host's configuration sources, and
/// how a malformed file is treated before and after startup.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class DeploymentLocalSettingsTests
{
    private string _root = null!;
    private string? _previousKeyPerFileDirectory;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "hvo-local-settings-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _previousKeyPerFileDirectory = Environment.GetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable);
        Environment.SetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable, null);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable, _previousKeyPerFileDirectory);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string SettingsPath => Path.Combine(_root, DeploymentLocalSettings.FileName);

    [TestMethod]
    public void ResolvePath_UsesAnExplicitAbsolutePath()
    {
        using var configuration = Named(SettingsPath);

        Assert.AreEqual(SettingsPath, DeploymentLocalSettings.ResolvePath(configuration, "/srv/app"));
    }

    [TestMethod]
    public void ResolvePath_RefusesARelativePath()
    {
        using var configuration = Named("App_Data/appsettings.local.json");

        Assert.ThrowsExactly<InvalidOperationException>(() => DeploymentLocalSettings.ResolvePath(configuration, "/srv/app"));
    }

    [TestMethod]
    public void ResolvePath_OutsideAnInstallerDeploymentWithoutAPath_IsNull()
    {
        using var configuration = new ConfigurationManager();

        Assert.IsNull(DeploymentLocalSettings.ResolvePath(configuration, "/srv/app"));
    }

    [TestMethod]
    public void ResolvePath_InAnInstallerDeployment_UsesTheAppDataMount()
    {
        Environment.SetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable, "/run/secrets/hvo");
        using var configuration = new ConfigurationManager();

        var path = DeploymentLocalSettings.ResolvePath(configuration, "/srv/app");

        Assert.AreEqual(Path.Combine("/srv/app", "App_Data", DeploymentLocalSettings.FileName), path);
    }

    [TestMethod]
    public void AddConfiguredFile_WithoutAPath_AddsNoSource()
    {
        using var configuration = new ConfigurationManager();
        var sources = configuration.Sources.Count;

        Assert.IsNull(DeploymentLocalSettings.AddConfiguredFile(configuration, _root));
        Assert.HasCount(sources, configuration.Sources);
    }

    [TestMethod]
    public async Task AddConfiguredFile_SitsAboveTheImageSettingsAndBelowLaterSources()
    {
        var image = Path.Combine(_root, "appsettings.json");
        await File.WriteAllTextAsync(image, """{ "A": "image", "B": "image", "C": "image" }""").ConfigureAwait(false);
        await File.WriteAllTextAsync(SettingsPath, """{ "A": "local", "B": "local" }""").ConfigureAwait(false);
        using var configuration = new ConfigurationManager();
        configuration.AddJsonFile(image);
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DeploymentLocalSettings.PathVariable] = SettingsPath,
            ["B"] = "environment"
        });

        var settings = DeploymentLocalSettings.AddConfiguredFile(configuration, _root);
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["D"] = "key-per-file" });

        Assert.IsNotNull(settings);
        Assert.AreEqual(SettingsPath, settings.FilePath);
        var sources = configuration.Sources.ToList();
        var imageIndex = sources.FindIndex(source => source is JsonConfigurationSource { Path: "appsettings.json" });
        Assert.AreEqual(
            DeploymentLocalSettings.FileName,
            Assert.IsInstanceOfType<JsonConfigurationSource>(sources[imageIndex + 1]).Path);
        Assert.AreEqual("local", configuration["A"]);
        Assert.AreEqual("environment", configuration["B"]);
        Assert.AreEqual("image", configuration["C"]);
        CollectionAssert.AreEqual(new[] { "B", "D" }, settings.FindOverriddenKeys(["A", "B", "C", "D"]).ToArray());
    }

    [TestMethod]
    public async Task AddConfiguredFile_WhenTheFileIsAbsent_LoadsItOnceItIsWrittenAndReloaded()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DeploymentLocalSettings.PathVariable] = SettingsPath
        });
        var settings = DeploymentLocalSettings.AddConfiguredFile(configuration, _root);
        Assert.IsNotNull(settings);
        Assert.IsNull(configuration["A"]);

        await File.WriteAllTextAsync(SettingsPath, """{ "A": "written" }""").ConfigureAwait(false);
        settings.Reload();

        Assert.AreEqual("written", configuration["A"]);
    }

    [TestMethod]
    public async Task AddConfiguredFile_WithAMalformedFile_StopsStartup()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "A": """).ConfigureAwait(false);
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DeploymentLocalSettings.PathVariable] = SettingsPath
        });

        Assert.ThrowsExactly<InvalidDataException>(() => DeploymentLocalSettings.AddConfiguredFile(configuration, _root));
    }

    [TestMethod]
    public async Task AMalformedEditAfterStartup_DoesNotStopTheHost()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "A": "valid" }""").ConfigureAwait(false);
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DeploymentLocalSettings.PathVariable] = SettingsPath
        });
        var settings = DeploymentLocalSettings.AddConfiguredFile(configuration, _root);
        Assert.IsNotNull(settings);

        await File.WriteAllTextAsync(SettingsPath, """{ "A": """).ConfigureAwait(false);
        settings.Reload();

        await File.WriteAllTextAsync(SettingsPath, """{ "A": "corrected" }""").ConfigureAwait(false);
        settings.Reload();
        Assert.AreEqual("corrected", configuration["A"]);
    }

    [TestMethod]
    public void AddConfiguredFile_InAMissingDirectory_Throws()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DeploymentLocalSettings.PathVariable] = Path.Combine(_root, "missing", DeploymentLocalSettings.FileName)
        });

        Assert.ThrowsExactly<InvalidOperationException>(() => DeploymentLocalSettings.AddConfiguredFile(configuration, _root));
    }

    private static ConfigurationManager Named(string path)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DeploymentLocalSettings.PathVariable] = path
        });
        return configuration;
    }
}
