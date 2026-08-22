using HVO.SkyMonitor.CameraAgent.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

internal sealed class OwnerBootstrapHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var stateReader = scope.ServiceProvider.GetRequiredService<OwnerBootstrapStateReader>();
        var state = await stateReader.GetStateAsync(cancellationToken).ConfigureAwait(false);
        return state == OwnerBootstrapStates.RecoveryRequired
            ? HealthCheckResult.Degraded("Owner bootstrap requires operator attention.")
            : HealthCheckResult.Healthy("Owner bootstrap is operational.");
    }
}
