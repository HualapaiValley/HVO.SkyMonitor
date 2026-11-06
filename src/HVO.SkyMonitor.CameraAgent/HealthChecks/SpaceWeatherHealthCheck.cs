using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

/// <summary>Projects optional SWPC health, always Healthy or Degraded.</summary>
public sealed class SpaceWeatherHealthCheck(SpaceWeatherHealthEvaluator evaluator) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = await evaluator.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Degraded ? HealthCheckResult.Degraded(snapshot.Description, data: snapshot.Data)
            : HealthCheckResult.Healthy(snapshot.Description, snapshot.Data);
    }
}
