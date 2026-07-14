using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

public sealed class RawIngressHealthCheck(RawIngressState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = state.Snapshot;
        var data = new Dictionary<string, object>
        {
            ["Availability"] = snapshot.Availability.ToString(),
            ["PendingCount"] = snapshot.PendingCount,
            ["PendingBytes"] = snapshot.PendingBytes,
            ["QuarantineCount"] = snapshot.QuarantineCount,
            ["QuarantineBytes"] = snapshot.QuarantineBytes
        };
        var result = snapshot.Availability switch
        {
            RawIngressAvailability.Accepting => HealthCheckResult.Healthy("Raw ingress is accepting captures.", data),
            RawIngressAvailability.Degraded => HealthCheckResult.Degraded("Raw ingress is accepting with reconciliation or pressure findings.", data: data),
            RawIngressAvailability.Initializing => HealthCheckResult.Degraded("Raw ingress has not completed initialization.", data: data),
            _ => HealthCheckResult.Unhealthy("Raw ingress cannot safely accept captures.", data: data)
        };
        return Task.FromResult(result);
    }
}
