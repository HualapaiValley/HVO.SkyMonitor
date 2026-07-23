using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

/// <summary>Reports whether protected deployment-location history selected an active immutable version.</summary>
public sealed class DeploymentLocationHealthCheck(IDeploymentLocationStore locationStore) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var active = locationStore.Active;
        var result = active is null
            ? HealthCheckResult.Degraded("Waiting for protected deployment-location history.")
            : HealthCheckResult.Healthy(
                "Protected deployment-location history is available.",
                new Dictionary<string, object>
                {
                    ["version"] = active.Version,
                    ["locationId"] = active.LocationId
                });
        return Task.FromResult(result);
    }
}
