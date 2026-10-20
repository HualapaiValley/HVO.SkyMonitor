using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
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
        services.TryAddSingleton<ICameraAgentOrbitalTimeSource, CameraAgentPinnedOrbitalTimeSource>();
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
}
