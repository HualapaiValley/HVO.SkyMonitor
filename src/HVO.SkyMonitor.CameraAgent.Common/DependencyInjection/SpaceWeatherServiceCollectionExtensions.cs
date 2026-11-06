using System.Collections.Immutable;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Fetch;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Policy;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;

/// <summary>Composes one local SWPC authority before the automation runner, including while disabled.</summary>
internal static class SpaceWeatherServiceCollectionExtensions
{
    internal const string StoreDirectoryName = ".space-weather";

    internal static IServiceCollection AddCameraAgentSpaceWeather(this IServiceCollection services)
    {
        services.AddSingleton(static provider => new SqliteSwpcStore(CreateStoreSettings(
            provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value), provider.GetRequiredService<ILogger<SqliteSwpcStore>>()));
        services.AddSingleton<ISwpcStore>(static provider => provider.GetRequiredService<SqliteSwpcStore>());
        services.AddSingleton<SpaceWeatherAvailability>();
        services.AddSingleton<SpaceWeatherWriteGate>();
        services.AddSingleton<SpaceWeatherTelemetry>();
        services.AddHttpClient(SwpcSource.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(SwpcSource.CreatePrimaryHandler)
            .ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan).RemoveAllLoggers();
        services.AddSingleton<ISwpcClient>(static provider => new SwpcClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(SwpcSource.HttpClientName),
            provider.GetRequiredService<TimeProvider>(), SwpcSource.UserAgent));
        services.AddSingleton(static provider => new SwpcAttemptRunner(provider.GetRequiredService<ISwpcStore>(),
            new SpaceWeatherMeasuredClient(provider.GetRequiredService<ISwpcClient>(), provider.GetRequiredService<SpaceWeatherTelemetry>(),
                provider.GetRequiredService<TimeProvider>()), provider.GetRequiredService<TimeProvider>(), new SwpcUniformJitter(new Random())));
        services.AddSingleton(static provider => new SpaceWeatherRefreshJob(provider.GetRequiredService<ISwpcStore>(),
            provider.GetRequiredService<SwpcAttemptRunner>(), provider.GetRequiredService<SpaceWeatherAvailability>(),
            provider.GetRequiredService<SpaceWeatherWriteGate>(), provider.GetRequiredService<IOptions<CameraAgentHostOptions>>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<SpaceWeatherTelemetry>(), provider.GetRequiredService<ILogger<SpaceWeatherRefreshJob>>()));
        services.AddSingleton<SpaceWeatherAutomationAdapter>();
        services.AddSingleton<ILocalAutomationPeriodicTaskAdapter>(static provider => provider.GetRequiredService<SpaceWeatherAutomationAdapter>());
        services.AddSingleton(static provider => new SpaceWeatherOperations(provider.GetRequiredService<ISwpcStore>(),
            provider.GetRequiredService<SwpcAttemptRunner>(), provider.GetRequiredService<SpaceWeatherAvailability>(),
            provider.GetRequiredService<SpaceWeatherWriteGate>(), provider.GetRequiredService<SpaceWeatherRefreshJob>(),
            provider.GetRequiredService<ILocalAutomationStore>(), provider.GetRequiredService<IOptions<CameraAgentHostOptions>>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<SpaceWeatherOperations>>()));
        services.AddSingleton<SpaceWeatherHealthEvaluator>();
        services.AddHostedService<SpaceWeatherInitializationService>();
        return services;
    }

    internal static SwpcStoreSettings CreateStoreSettings(CameraAgentHostOptions options) => new(
        Path.Combine(Path.GetFullPath(options.RawIngressRoot), StoreDirectoryName), Enum.GetValues<SwpcProduct>().ToImmutableDictionary(
            static product => product, static product => new SwpcFreshnessThresholds(SwpcSource.BaseInterval(product), SwpcSource.OutdatedAfter(product))));
}
