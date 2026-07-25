using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

internal sealed class DeploymentLocationReconciliationHealthCheck(
    DeploymentLocationReconciliationState state,
    IOptions<CameraAgentHostOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (options.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "Deployment-location central reconciliation is not active."));
        }
        if (state.LastAttemptUtc is null)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "Deployment-location central reconciliation has not completed an attempt."));
        }
        return Task.FromResult(state.Outcome == "acknowledged"
            ? HealthCheckResult.Healthy("Deployment location is acknowledged by LogicHost.")
            : HealthCheckResult.Degraded("Deployment-location central reconciliation is pending or offline."));
    }
}
