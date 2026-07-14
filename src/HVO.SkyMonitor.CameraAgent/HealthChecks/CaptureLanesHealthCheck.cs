using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

public sealed class CaptureLanesHealthCheck(CaptureLaneState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = state.Snapshot;
        var data = new Dictionary<string, object>
        {
            ["Availability"] = snapshot.Availability.ToString(),
            ["LaneCount"] = snapshot.Lanes.Count,
            ["PendingCount"] = snapshot.PendingCount,
            ["PendingBytes"] = snapshot.PendingBytes,
            ["LeasedCount"] = snapshot.LeasedCount,
            ["QuarantineCount"] = snapshot.QuarantineCount
        };
        var result = snapshot.Availability switch
        {
            CaptureLaneAvailability.Healthy => HealthCheckResult.Healthy("Capture lanes are accepting and draining work.", data),
            CaptureLaneAvailability.Degraded => HealthCheckResult.Degraded("Capture lanes have optional or warning-level pressure.", data: data),
            CaptureLaneAvailability.Initializing => HealthCheckResult.Degraded("Capture lanes have not completed initialization.", data: data),
            _ => HealthCheckResult.Unhealthy("A required capture lane cannot safely accept work.", data: data)
        };
        return Task.FromResult(result);
    }
}
