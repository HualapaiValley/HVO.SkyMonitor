using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

/// <summary>Projects the host-neutral orbital-elements health evaluation onto ASP.NET Core health checks.</summary>
public sealed class OrbitalElementsHealthCheck(OrbitalElementsHealthEvaluator evaluator) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await evaluator.EvaluateAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.State switch
        {
            OrbitalElementsHealthState.Healthy => HealthCheckResult.Healthy(snapshot.Description, snapshot.Data),
            OrbitalElementsHealthState.Degraded => HealthCheckResult.Degraded(snapshot.Description, data: snapshot.Data),
            _ => HealthCheckResult.Unhealthy(snapshot.Description, data: snapshot.Data)
        };
    }
}
