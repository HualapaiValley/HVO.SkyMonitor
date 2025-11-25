using System;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HVO.SkyMonitor.CameraAgent.Tests.Configuration;

[TestClass]
public sealed class DeviceSecretsCentralIdentityConfiguratorTests
{
    [TestMethod]
    public void Configure_OverridesOptions_WhenSecretsPresent()
    {
        var storedOptions = new CentralIdentityOptions
        {
            ServiceUrl = new Uri("https://hvo-skymonitor.b2clogin.com/tenant/policy/v2.0/"),
            TokenEndpoint = new Uri("https://hvo-skymonitor.b2clogin.com/tenant/oauth2/v2.0/token?p=policy"),
            Mode = AuthenticationMode.ClientCredentials,
            ClientCredentials = new ClientCredentialsOptions
            {
                ClientId = "camera-agent",
                ClientSecret = "secret-value"
            },
            InteractiveClient = new InteractiveClientOptions
            {
                ClientId = "interactive-id",
                ClientSecret = "interactive-secret",
                CallbackPath = "/signin-central",
                SignedOutCallbackPath = "/signout-callback-central",
                RemoteSignOutPath = "/signout-central"
            }
        };
        storedOptions.ClientCredentials!.Scopes.Add("api.camera");
        storedOptions.InteractiveClient!.Scopes.Clear();
        storedOptions.InteractiveClient!.Scopes.Add("openid");

        var secrets = new DeviceSecrets(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Agent",
            "ticket",
            "/api/device/heartbeat",
            "/api/device/upload",
            60,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(1),
            "device-key",
            storedOptions);

        var store = new StubSecretStore(secrets);
        var configurator = new DeviceSecretsCentralIdentityConfigurator(store, NullLogger<DeviceSecretsCentralIdentityConfigurator>.Instance);

        var options = new CentralIdentityOptions
        {
            ServiceUrl = new Uri("https://localhost:7096"),
            ClientCredentials = new ClientCredentialsOptions
            {
                ClientId = "old",
                ClientSecret = "old"
            },
            InteractiveClient = new InteractiveClientOptions
            {
                ClientId = "old",
                ClientSecret = "old",
                CallbackPath = "/signin-old",
                SignedOutCallbackPath = "/signout-old",
                RemoteSignOutPath = "/signout-remote-old"
            }
        };
        options.ClientCredentials!.Scopes.Add("api.old");
        options.InteractiveClient!.Scopes.Add("openid");

        configurator.Configure(options);

        Assert.AreEqual(storedOptions.ServiceUrl, options.ServiceUrl);
        Assert.AreEqual(storedOptions.TokenEndpoint, options.TokenEndpoint);
        Assert.AreEqual("camera-agent", options.ClientCredentials!.ClientId);
        Assert.AreEqual(1, options.ClientCredentials.Scopes.Count);
        Assert.AreEqual("api.camera", options.ClientCredentials.Scopes[0]);
        Assert.AreEqual("interactive-id", options.InteractiveClient!.ClientId);
        Assert.AreEqual("interactive-secret", options.InteractiveClient.ClientSecret);
    }

    [TestMethod]
    public void Configure_DoesNothing_WhenSecretsMissing()
    {
        var store = new StubSecretStore(null);
        var configurator = new DeviceSecretsCentralIdentityConfigurator(store, NullLogger<DeviceSecretsCentralIdentityConfigurator>.Instance);
        var options = new CentralIdentityOptions
        {
            ServiceUrl = new Uri("https://localhost:7096"),
            ClientCredentials = new ClientCredentialsOptions
            {
                ClientId = "unchanged",
                ClientSecret = "unchanged"
            }
        };
        options.ClientCredentials!.Scopes.Add("api.camera");

        configurator.Configure(options);

        Assert.AreEqual("unchanged", options.ClientCredentials!.ClientId);
        Assert.AreEqual("https://localhost:7096/", options.ServiceUrl.ToString());
    }

    private sealed class StubSecretStore : IDeviceSecretStore
    {
        private readonly DeviceSecrets? secrets;

        public StubSecretStore(DeviceSecrets? secrets)
        {
            this.secrets = secrets;
        }

        public Task<DeviceSecrets?> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(secrets);

        public Task SaveAsync(DeviceSecrets secrets, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task ClearAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
