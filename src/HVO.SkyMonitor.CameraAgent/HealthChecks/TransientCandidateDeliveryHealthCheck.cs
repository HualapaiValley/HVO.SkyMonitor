using HVO.SkyMonitor.CameraAgent.Common.Transients;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

public sealed class TransientCandidateDeliveryHealthCheck(TransientCandidateDeliveryState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = state.Snapshot;
        var data = new Dictionary<string, object>
        {
            ["availability"] = snapshot.Availability.ToString(),
            ["reason"] = snapshot.Reason,
            ["pendingCount"] = snapshot.PendingCount,
            ["retryingCount"] = snapshot.RetryingCount,
            ["authenticationBlockedCount"] = snapshot.AuthenticationBlockedCount,
            ["quarantinedCount"] = snapshot.QuarantinedCount,
            ["oldestPendingUtc"] = snapshot.OldestPendingUtc ?? DateTimeOffset.MinValue,
            ["lastAcknowledgedUtc"] = snapshot.LastAcknowledgedUtc ?? DateTimeOffset.MinValue,
            ["lastAttemptUtc"] = snapshot.LastAttemptUtc ?? DateTimeOffset.MinValue,
            ["lastScanUtc"] = snapshot.LastScanUtc ?? DateTimeOffset.MinValue
        };
        return Task.FromResult(snapshot.Availability switch
        {
            TransientCandidateDeliveryAvailability.Unhealthy =>
                HealthCheckResult.Unhealthy(snapshot.Reason, data: data),
            TransientCandidateDeliveryAvailability.Degraded or TransientCandidateDeliveryAvailability.Starting =>
                HealthCheckResult.Degraded(snapshot.Reason, data: data),
            _ => HealthCheckResult.Healthy(snapshot.Reason, data)
        });
    }
}
