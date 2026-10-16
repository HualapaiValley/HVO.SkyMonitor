using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Common.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Configuration;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class LocalIdentityPasswordFileTests
{
    [TestMethod]
    public void ApplyLocalIdentityPasswordFile_ReadsSecretWithoutRetainingNewline()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "AcceptancePassword!197\n");
            var configuration = new ConfigurationManager();
            configuration["LocalIdentity:AdminPasswordFile"] = path;

            Program.ApplyLocalIdentityPasswordFile(configuration);

            Assert.AreEqual("AcceptancePassword!197", configuration["LocalIdentity:AdminPassword"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ApplyLocalIdentityPasswordFile_ReadsTheInstallerDefaultAsAValidTemporaryPassword()
    {
        var path = Path.GetTempFileName();
        try
        {
            // The installer writes the documented default exactly like this when no password option is given.
            File.WriteAllText(path, "skymonitor\n");
            var configuration = new ConfigurationManager();
            configuration["LocalIdentity:AdminPasswordFile"] = path;

            Program.ApplyLocalIdentityPasswordFile(configuration);

            Assert.AreEqual("skymonitor", configuration["LocalIdentity:AdminPassword"]);
            var options = new HVO.SkyMonitor.CameraAgent.Configuration.LocalIdentityOptions
            {
                AdminPassword = configuration["LocalIdentity:AdminPassword"]!
            };
            var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
            Assert.IsTrue(
                System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
                    options,
                    new System.ComponentModel.DataAnnotations.ValidationContext(options),
                    results,
                    validateAllProperties: true),
                string.Join("; ", results));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    [DataRow("  leading and inner\n", "  leading and inner")]
    [DataRow("  leading and inner\r\n", "  leading and inner")]
    [DataRow("trailing and inner  \n", "trailing and inner  ")]
    [DataRow("trailing and inner  \r\n", "trailing and inner  ")]
    [DataRow("\uFEFFwith-byte-order-mark\n", "with-byte-order-mark")]
    [DataRow("no-line-ending", "no-line-ending")]
    public void ApplyLocalIdentityPasswordFile_KeepsSignificantSpacesAndRemovesOnlyTheLineEnding(
        string content,
        string expectedPassword)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(expectedPassword);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, content);
            var configuration = new ConfigurationManager();
            configuration["LocalIdentity:AdminPasswordFile"] = path;

            Program.ApplyLocalIdentityPasswordFile(configuration);

            Assert.AreEqual(expectedPassword, configuration["LocalIdentity:AdminPassword"]);
            Assert.AreEqual(
                expectedPassword,
                HVO.SkyMonitor.CameraAgent.Configuration.OwnerPasswordFileText.Decode(File.ReadAllBytes(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ApplyLocalIdentityPasswordFile_RejectsContentThatIsNotUtf8()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0xFF, 0x0A]);
            var configuration = new ConfigurationManager();
            configuration["LocalIdentity:AdminPasswordFile"] = path;

            var exception = Assert.ThrowsExactly<InvalidOperationException>(
                () => Program.ApplyLocalIdentityPasswordFile(configuration));

            Assert.AreEqual("LocalIdentity:AdminPasswordFile is not valid UTF-8.", exception.Message);
            Assert.IsNull(configuration["LocalIdentity:AdminPassword"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ApplyLocalIdentityPasswordFile_RejectsRelativePath()
    {
        var configuration = new ConfigurationManager();
        configuration["LocalIdentity:AdminPasswordFile"] = "relative-password.txt";

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => Program.ApplyLocalIdentityPasswordFile(configuration));

        StringAssert.Contains(exception.Message, "absolute path", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ApplyLocalIdentityPasswordFile_RejectsEmptyFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            var configuration = new ConfigurationManager();
            configuration["LocalIdentity:AdminPasswordFile"] = path;

            var exception = Assert.ThrowsExactly<InvalidOperationException>(
                () => Program.ApplyLocalIdentityPasswordFile(configuration));

            StringAssert.Contains(exception.Message, "non-empty regular file", StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task DeploymentKeyPerFile_BindsHostOptionsAndLoadsSeparateModuleDocument()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-camera-kpf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable);
        try
        {
            var modulePath = Path.Combine(AppContext.BaseDirectory, "cameraagent.sample.json");
            await File.WriteAllTextAsync(Path.Combine(root, "CameraAgent__ConfigFilePath"), modulePath).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(root, "CameraAgent__RawIngressRoot"), "/app/data/raw").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(root, "CameraAgent__ProvisioningStartupGate__Enabled"), "true").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(root, "CameraAgent__CaptureDistribution__UploadEnabled"), "false").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(root, "CameraAgent__CentralIntegration__Mode"), "Disabled").ConfigureAwait(false);
            Environment.SetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable, root);
            var configuration = new ConfigurationManager();

            DeploymentKeyPerFile.AddConfiguredDirectory(configuration);
            var options = configuration.GetSection("CameraAgent").Get<CameraAgentHostOptions>();

            Assert.IsNotNull(options);
            Assert.AreEqual(modulePath, options.ConfigFilePath);
            Assert.AreEqual("/app/data/raw", options.RawIngressRoot);
            Assert.IsTrue(options.ProvisioningStartupGate.Enabled);
            Assert.IsFalse(options.CaptureDistribution.UploadEnabled);
            Assert.AreEqual(CentralIntegrationMode.Disabled, options.CentralIntegration.Mode);
            var loaded = await new FileCameraAgentConfigurationLoader(
                Microsoft.Extensions.Options.Options.Create(options),
                NullLogger<FileCameraAgentConfigurationLoader>.Instance).LoadAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual("VirtualSky", loaded.Module.Type);
        }
        finally
        {
            Environment.SetEnvironmentVariable(DeploymentKeyPerFile.DirectoryVariable, previous);
            Directory.Delete(root, recursive: true);
        }
    }
}
