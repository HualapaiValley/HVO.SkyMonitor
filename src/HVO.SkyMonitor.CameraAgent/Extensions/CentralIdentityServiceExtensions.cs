using System;
using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Extensions;

/// <summary>
/// Extension methods for configuring central identity authentication in camera agents.
/// </summary>
public static class CentralIdentityServiceExtensions
{
    /// <summary>
    /// Adds central identity authentication services to the service collection.
    /// Configures the camera agent to authenticate with the central HVO.SkyMonitor identity service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration containing CentralIdentity section.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCentralIdentityAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<CentralIdentityOptions>(
            configuration.GetSection("CentralIdentity"));
        services.PostConfigure<CentralIdentityOptions>(ApplyDefaultScopes);

        services.AddHttpClient();
        services.AddHttpClient(CentralAuthenticationService.TokenClientName);
        services.AddSingleton<ICentralAuthenticationService, CentralAuthenticationService>();
        services.AddSingleton<ICentralIdentityNavigationService, CentralIdentityNavigationService>();

        return services;
    }

    /// <summary>
    /// Adds central identity authentication services with explicit configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Action to configure the central identity options.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddCentralIdentityAuthentication(
        this IServiceCollection services,
        Action<CentralIdentityOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.Configure(configureOptions);
        services.PostConfigure<CentralIdentityOptions>(ApplyDefaultScopes);
        services.AddHttpClient();
        services.AddHttpClient(CentralAuthenticationService.TokenClientName);
        services.AddSingleton<ICentralAuthenticationService, CentralAuthenticationService>();
        services.AddSingleton<ICentralIdentityNavigationService, CentralIdentityNavigationService>();

        return services;
    }

    private static void ApplyDefaultScopes(CentralIdentityOptions options)
    {
        if (options.ClientCredentials is not { } credentials)
        {
            return;
        }

        if (credentials.Scopes.Count > 0)
        {
            return;
        }

        foreach (var scope in ClientCredentialsOptions.DefaultScopes)
        {
            credentials.Scopes.Add(scope);
        }
    }
}
