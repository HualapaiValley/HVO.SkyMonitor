using HVO.SkyMonitor.CameraAgent.Common.Transients;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

public sealed class TransientWorkerHealthCheck(TransientWorkerState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = state.Snapshot;
        var data = new Dictionary<string, object>
        {
            ["reason"] = snapshot.Reason,
            ["pendingFrames"] = snapshot.PendingFrames,
            ["pendingCandidates"] = snapshot.PendingCandidates,
            ["updatedUtc"] = snapshot.UpdatedUtc
        };
        return Task.FromResult(snapshot.Availability switch
        {
            TransientWorkerAvailability.Unhealthy => HealthCheckResult.Unhealthy(snapshot.Reason, data: data),
            TransientWorkerAvailability.Degraded => HealthCheckResult.Degraded(snapshot.Reason, data: data),
            _ => HealthCheckResult.Healthy(snapshot.Reason, data)
        });
    }
}
