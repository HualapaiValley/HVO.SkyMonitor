using System;
using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.CameraAgent.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Extensions;

/// <summary>
/// DI helpers for configuring outbound SkyMonitor HTTP clients.
/// </summary>
public static class SkyMonitorClientServiceExtensions
{
    public static IHttpClientBuilder AddSkyMonitorApiClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        RegisterOptions(services, configuration);
        services.AddTransient<CentralIdentityDelegatingHandler>();

        return services
            .AddHttpClient(SkyMonitorClientOptions.HttpClientName)
            .ConfigureHttpClient((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptionsMonitor<SkyMonitorClientOptions>>().CurrentValue;
                client.BaseAddress = options.ResolveBaseUri();
            })
            .AddHttpMessageHandler<CentralIdentityDelegatingHandler>();
    }

    public static IHttpClientBuilder AddSkyMonitorApiClient(
        this IServiceCollection services,
        Action<SkyMonitorClientOptions> configureOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureOptions);

        services.AddOptions<SkyMonitorClientOptions>()
            .Configure(configureOptions)
            .Validate(static options => options.TryResolveBaseUri(out _), "SkyMonitor:BaseUrl must be a valid absolute URI.")
            .ValidateOnStart();

        services.AddTransient<CentralIdentityDelegatingHandler>();

        return services
            .AddHttpClient(SkyMonitorClientOptions.HttpClientName)
            .ConfigureHttpClient((provider, client) =>
            {
                var options = provider.GetRequiredService<IOptionsMonitor<SkyMonitorClientOptions>>().CurrentValue;
                client.BaseAddress = options.ResolveBaseUri();
            })
            .AddHttpMessageHandler<CentralIdentityDelegatingHandler>();
    }

    private static void RegisterOptions(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SkyMonitorClientOptions>()
            .Bind(configuration.GetSection(SkyMonitorClientOptions.SectionName))
            .Validate(static options => options.TryResolveBaseUri(out _), "SkyMonitor:BaseUrl must be a valid absolute URI.")
            .ValidateOnStart();
    }
}
