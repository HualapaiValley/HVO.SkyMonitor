using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.AgentCore;
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
            var observatoryId = Guid.NewGuid();
            var expected = new DeviceSecrets(
                Guid.NewGuid(),
                observatoryId,
                "Test device",
                "registration-token",
                "/api/device/heartbeat",
                60,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(1),
                "device-key",
                new CentralIdentityOptions
                {
                    ServiceUrl = new Uri("https://identity.example", UriKind.Absolute)
                },
                DeploymentLocationAcknowledgment: CreateAcknowledgment(observatoryId));

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
    public async Task GetAsync_PreChangeProtectedSecretsRemainReadableForReconciliation()
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

            var actual = await store.GetAsync().ConfigureAwait(false);

            Assert.IsNotNull(actual);
            Assert.IsNull(actual.DeploymentLocationAcknowledgment);
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
    [DataRow("foreign-observatory")]
    [DataRow("unspecified-source")]
    public async Task SaveAsync_RejectsNoncanonicalLocationAcknowledgmentWithoutWriting(string scenario)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-device-secrets-invalid-{Guid.NewGuid():N}");
        var keyDirectory = Path.Combine(root, "keys");
        var stateDirectory = Path.Combine(root, "state");
        try
        {
            var observatoryId = Guid.NewGuid();
            var acknowledgment = CreateAcknowledgment(
                scenario == "foreign-observatory" ? Guid.NewGuid() : observatoryId);
            if (scenario == "unspecified-source")
            {
                acknowledgment = acknowledgment with { SourceKind = DeploymentLocationSourceKind.Unspecified };
            }
            var secrets = new DeviceSecrets(
                Guid.NewGuid(),
                observatoryId,
                "Invalid device",
                "registration-token",
                "/api/device/heartbeat",
                60,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(1),
                "device-key",
                new CentralIdentityOptions(),
                DeploymentLocationAcknowledgment: acknowledgment);
            var options = Options.Create(new DeviceProvisioningOptions { StateDirectory = stateDirectory });
            using var services = CreateProvider(keyDirectory);
            var store = new DeviceSecretStore(
                services.GetRequiredService<IDataProtectionProvider>(),
                options,
                NullLogger<DeviceSecretStore>.Instance);

            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(secrets)).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(options.Value.GetSecretsPath()));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static DeploymentLocationAcknowledgment CreateAcknowledgment(Guid observatoryId)
    {
        var deployment = DeploymentLocationSnapshot.Create(
            "device-secret-test",
            1,
            "manual test",
            5,
            DateTimeOffset.UnixEpoch,
            null,
            35.347,
            -113.878,
            520,
            "America/Phoenix");
        return new DeploymentLocationAcknowledgment(
            ObservatoryLocationSnapshot.Create(
                observatoryId,
                1,
                DateTimeOffset.UnixEpoch,
                deployment.LatitudeDegrees,
                deployment.LongitudeDegrees,
                deployment.ElevationMeters,
                deployment.TimeZoneId,
                null),
            deployment,
            DeploymentLocationSourceKind.Manual,
            DeploymentLocationResolutionStatus.Pending,
            "boundary-unconfigured",
            DateTimeOffset.UtcNow,
            null);
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
