using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

/// <summary>Reports whether protected deployment-location history selected a currently usable immutable version.</summary>
public sealed class DeploymentLocationHealthCheck(
    IDeploymentLocationStore locationStore,
    TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var active = locationStore.Active;
        var result = active switch
        {
            null => HealthCheckResult.Degraded("Waiting for protected deployment-location history."),
            _ when !active.Validate().IsValid || !active.IsEffectiveAt(timeProvider.GetUtcNow()) =>
                HealthCheckResult.Degraded(
                    "The active protected deployment-location version is not currently usable.",
                    data: new Dictionary<string, object> { ["version"] = active.Version }),
            _ => HealthCheckResult.Healthy(
                "Protected deployment-location history is available.",
                new Dictionary<string, object>
                {
                    ["version"] = active.Version,
                    ["locationId"] = active.LocationId
                })
        };
        return Task.FromResult(result);
    }
}
