using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class DeviceSecretStorePersistenceTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task SaveAndReload_WithPersistedKeyRing_SurvivesProviderRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-device-secrets-{Guid.NewGuid():N}");
        var keyDirectory = Path.Combine(root, "keys");
        var stateDirectory = Path.Combine(root, "state");

        try
        {
            var options = Options.Create(new DeviceProvisioningOptions
            {
                StateDirectory = stateDirectory
            });
            var expected = new DeviceSecrets(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Test device",
                "registration-token",
                "/api/device/heartbeat",
                "/api/device/upload",
                60,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(1),
                "device-key",
                new CentralIdentityOptions
                {
                    ServiceUrl = new Uri("https://identity.example", UriKind.Absolute)
                });

            using (var services = CreateProvider(keyDirectory))
            {
                var store = new DeviceSecretStore(
                    services.GetRequiredService<IDataProtectionProvider>(),
                    options,
                    NullLogger<DeviceSecretStore>.Instance);
                await store.SaveAsync(expected).ConfigureAwait(false);
            }

            using (var services = CreateProvider(keyDirectory))
            {
                var store = new DeviceSecretStore(
                    services.GetRequiredService<IDataProtectionProvider>(),
                    options,
                    NullLogger<DeviceSecretStore>.Instance);
                var actual = await store.GetAsync().ConfigureAwait(false);
                Assert.IsNotNull(actual);
                Assert.AreEqual(expected.DevicePublicId, actual.DevicePublicId);
                Assert.AreEqual(expected.ObservatoryId, actual.ObservatoryId);
                Assert.AreEqual(expected.RegistrationToken, actual.RegistrationToken);
                Assert.AreEqual(expected.DeviceKey, actual.DeviceKey);
                Assert.AreEqual(expected.CentralIdentity.ServiceUrl, actual.CentralIdentity.ServiceUrl);
            }

            if (!OperatingSystem.IsWindows())
            {
                Assert.AreEqual(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(stateDirectory));
                Assert.AreEqual(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(stateDirectory, "device-secrets.dat")));
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task GetAsync_PreChangeProtectedSecretsLoadsWithNullLocationAcknowledgment()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-device-secrets-legacy-{Guid.NewGuid():N}");
        var keyDirectory = Path.Combine(root, "keys");
        var stateDirectory = Path.Combine(root, "state");
        try
        {
            var options = Options.Create(new DeviceProvisioningOptions { StateDirectory = stateDirectory });
            using var services = CreateProvider(keyDirectory);
            var provider = services.GetRequiredService<IDataProtectionProvider>();
            var protector = provider.CreateProtector("CameraAgent", "DeviceSecrets", "v1");
            Directory.CreateDirectory(stateDirectory);
            var legacy = new
            {
                devicePublicId = Guid.NewGuid(),
                observatoryId = Guid.NewGuid(),
                friendlyName = "Legacy camera",
                registrationToken = "registration-token",
                heartbeatEndpoint = "/api/device/heartbeat",
                uploadEndpoint = "/api/device/upload",
                heartbeatIntervalSeconds = 60,
                issuedAtUtc = DateTimeOffset.UtcNow,
                expiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
                deviceKey = "device-key",
                centralIdentity = new CentralIdentityOptions(),
                rigProfileEndpoint = "/api/device/profile/rig"
            };
            await File.WriteAllTextAsync(
                options.Value.GetSecretsPath(),
                protector.Protect(JsonSerializer.Serialize(legacy, SerializerOptions)))
                .ConfigureAwait(false);
            var store = new DeviceSecretStore(provider, options, NullLogger<DeviceSecretStore>.Instance);

            var loaded = await store.GetAsync().ConfigureAwait(false);

            Assert.IsNotNull(loaded);
            Assert.IsNull(loaded.DeploymentLocationAcknowledgment);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ServiceProvider CreateProvider(string keyDirectory)
    {
        var services = new ServiceCollection();
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
            .SetApplicationName("HVO.SkyMonitor.CameraAgent.Tests");
        return services.BuildServiceProvider();
    }
}
