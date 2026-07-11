using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

/// <summary>Reports whether the configuration-driven capture pipeline has loaded a valid agent configuration.</summary>
public sealed class CameraAgentConfigurationHealthCheck(ICameraAgentConfigurationAccessor configurationAccessor) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = configurationAccessor.IsConfigured
            ? HealthCheckResult.Healthy("Camera module and processing configuration are loaded.")
            : HealthCheckResult.Degraded("Waiting for camera module and processing configuration.");
        return Task.FromResult(result);
    }
}
