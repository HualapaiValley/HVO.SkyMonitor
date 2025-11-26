using System;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Configuration;

/// <summary>
/// Hydrates <see cref="CentralIdentityOptions"/> from device bootstrap secrets so CameraAgent instances inherit LogicHost Central Identity wiring.
/// </summary>
internal sealed class DeviceSecretsCentralIdentityConfigurator : IConfigureOptions<CentralIdentityOptions>
{
    private readonly IDeviceSecretStore secretStore;
    private readonly ILogger<DeviceSecretsCentralIdentityConfigurator> logger;

    public DeviceSecretsCentralIdentityConfigurator(
        IDeviceSecretStore secretStore,
        ILogger<DeviceSecretsCentralIdentityConfigurator> logger)
    {
        this.secretStore = secretStore;
        this.logger = logger;
    }

    public void Configure(CentralIdentityOptions options)
    {
        DeviceSecrets? secrets = null;
        try
        {
            secrets = secretStore.GetAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load device secrets while configuring Central Identity overrides.");
        }

        var centralIdentity = secrets?.CentralIdentity;
        if (centralIdentity is null)
        {
            return;
        }

        ApplyCentralIdentity(centralIdentity, options);
        logger.LogInformation("Central Identity settings supplied by device bootstrap secrets.");
    }

    private static void ApplyCentralIdentity(CentralIdentityOptions source, CentralIdentityOptions target)
    {
        target.ServiceUrl = source.ServiceUrl;
        target.TokenEndpoint = source.TokenEndpoint;
        target.Mode = source.Mode;
        target.TokenCacheDurationSeconds = source.TokenCacheDurationSeconds;
        target.TokenRefreshWindowSeconds = source.TokenRefreshWindowSeconds;
        target.ClientCredentials = CloneClientCredentials(source.ClientCredentials);
        target.ApiKey = CloneApiKey(source.ApiKey);
        target.InteractiveClient = CloneInteractiveClient(source.InteractiveClient);
        target.LocalFallback = CloneLocalFallback(source.LocalFallback);
    }

    private static ClientCredentialsOptions? CloneClientCredentials(ClientCredentialsOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        var clone = new ClientCredentialsOptions
        {
            ClientId = options.ClientId,
            ClientSecret = options.ClientSecret
        };

        clone.Scopes.Clear();
        foreach (var scope in options.Scopes)
        {
            clone.Scopes.Add(scope);
        }

        return clone;
    }

    private static ApiKeyOptions? CloneApiKey(ApiKeyOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        return new ApiKeyOptions
        {
            Key = options.Key
        };
    }

    private static InteractiveClientOptions? CloneInteractiveClient(InteractiveClientOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        var clone = new InteractiveClientOptions
        {
            ClientId = options.ClientId,
            ClientSecret = options.ClientSecret,
            CallbackPath = options.CallbackPath,
            SignedOutCallbackPath = options.SignedOutCallbackPath,
            RemoteSignOutPath = options.RemoteSignOutPath,
            PublicAuthority = options.PublicAuthority
        };

        clone.Scopes.Clear();
        foreach (var scope in options.Scopes)
        {
            clone.Scopes.Add(scope);
        }

        return clone;
    }

    private static LocalFallbackOptions CloneLocalFallback(LocalFallbackOptions? options)
    {
        if (options is null)
        {
            return new LocalFallbackOptions();
        }

        return new LocalFallbackOptions
        {
            AccessCodeHash = options.AccessCodeHash,
            LastRotatedUtc = options.LastRotatedUtc,
            RotationIntervalDays = options.RotationIntervalDays
        };
    }
}
