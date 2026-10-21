using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

/// <summary>One numerical engine pair constructed from the same immutable resolved time inputs.</summary>
public sealed record CameraAgentOrbitalPlanningEngines(IOrbitalPredictionEngine Prediction, IOrbitalPassArcEngine Arcs);

public interface ICameraAgentOrbitalPlanningEngineFactory
{
    CameraAgentOrbitalPlanningEngines Create(OrbitalTimeData timeData);
}

public sealed class CameraAgentOrbitalPlanningEngineFactory(TimeProvider timeProvider) : ICameraAgentOrbitalPlanningEngineFactory
{
    public CameraAgentOrbitalPlanningEngines Create(OrbitalTimeData timeData)
    {
        var engine = new OrbitalPredictionEngine(timeData, timeProvider);
        return new(engine, engine);
    }
}

public static class CameraAgentSatellitePlanningExtensions
{
    /// <summary>
    /// Adds a singleton, single-flight local planner. An optional GP reader supplies neither time inputs nor engines.
    /// The host must already register its authoritative sky-map/configuration services.
    /// </summary>
    public static IServiceCollection AddCameraAgentSatellitePlanning(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.Configure<CameraAgentSatellitePlanningOptions>(configuration.GetSection(CameraAgentSatellitePlanningOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        // The managed pair is used when the host also adds the time-data refresh; planning never downloads.
        services.TryAddSingleton<ICameraAgentOrbitalTimeSource>(static provider => new CameraAgentPinnedOrbitalTimeSource(
            provider.GetRequiredService<IOptionsMonitor<CameraAgentSatellitePlanningOptions>>(),
            provider.GetService<CameraAgentManagedTimeDataStore>()));
        services.TryAddSingleton<ICameraAgentOrbitalPlanningEngineFactory, CameraAgentOrbitalPlanningEngineFactory>();
        services.TryAddSingleton<ICameraAgentSatellitePlanner>(provider => new CameraAgentSatellitePlanner(
            provider.GetRequiredService<ICameraAgentSkyMapProjection>(),
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>(),
            provider.GetRequiredService<ICameraAgentOrbitalTimeSource>(),
            provider.GetRequiredService<ICameraAgentOrbitalPlanningEngineFactory>(),
            provider.GetRequiredService<IOptionsMonitor<CameraAgentSatellitePlanningOptions>>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetService<IOrbitalSnapshotReader>(),
            provider.GetService<CaptureScheduleRuntimeCoordinator>()));
        return services;
    }

    /// <summary>
    /// Adds the managed, separately enabled time-data refresh: its store under <c>{RawIngressRoot}/.time-data/</c>, the
    /// approved-endpoint client, the single-flight job, its reserved daily automation adapter and the read-only status.
    /// Call after <see cref="AddCameraAgentSatellitePlanning"/> and before the host is built; the automation runner
    /// resolves the adapter. Nothing downloads until the switch is on and the automation exists.
    /// </summary>
    public static IServiceCollection AddCameraAgentTimeDataRefresh(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        // The managed pair lives with the raw-ingress state, so it survives restarts and upgrades.
        services.TryAddSingleton(static provider => new CameraAgentManagedTimeDataStore(Path.Combine(
            Path.GetFullPath(provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.RawIngressRoot is { Length: > 0 } root
                ? root : throw new InvalidOperationException("CameraAgent:RawIngressRoot is required for managed time data.")),
            CameraAgentManagedTimeDataStore.DirectoryName)));
        // No retry, resilience, redirect or logging handlers: each download is one request under the client's own deadline.
        services.AddHttpClient(CameraAgentTimeDataSources.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(CameraAgentTimeDataSources.CreatePrimaryHandler)
            .ConfigureHttpClient(static client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers();
        services.TryAddSingleton<ICameraAgentTimeDataClient>(static provider => new CameraAgentTimeDataClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(CameraAgentTimeDataSources.HttpClientName),
            provider.GetRequiredService<TimeProvider>(), OrbitalElementsServiceCollectionExtensions.UserAgent));
        services.TryAddSingleton<CameraAgentTimeDataRefreshJob>();
        services.TryAddSingleton<CameraAgentTimeDataAutomationAdapter>();
        services.AddSingleton<ILocalAutomationPeriodicTaskAdapter>(static provider =>
            provider.GetRequiredService<CameraAgentTimeDataAutomationAdapter>());
        services.TryAddSingleton(static provider => new CameraAgentTimeDataOperations(
            provider.GetRequiredService<ICameraAgentOrbitalTimeSource>(), provider.GetRequiredService<CameraAgentManagedTimeDataStore>(),
            provider.GetRequiredService<CameraAgentTimeDataRefreshJob>(),
            provider.GetRequiredService<IOptionsMonitor<CameraAgentSatellitePlanningOptions>>(),
            provider.GetRequiredService<TimeProvider>(), provider.GetService<ILocalAutomationStore>()));
        return services;
    }
}
