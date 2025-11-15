using HVO.SkyMonitor.CameraAgent.Authentication;
using HVO.SkyMonitor.CameraAgent.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
        services.Configure<CentralIdentityOptions>(
            configuration.GetSection("CentralIdentity"));

        services.AddHttpClient();
        services.AddSingleton<ICentralAuthenticationService, CentralAuthenticationService>();

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
        services.Configure(configureOptions);
        services.AddHttpClient();
        services.AddSingleton<ICentralAuthenticationService, CentralAuthenticationService>();

        return services;
    }
}
