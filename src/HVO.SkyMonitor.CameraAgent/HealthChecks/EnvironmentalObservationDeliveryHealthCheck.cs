using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

public sealed class EnvironmentalObservationDeliveryHealthCheck(
    EnvironmentalObservationDeliveryState state,
    TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = state.Snapshot;
        var outbox = snapshot.Outbox;
        var data = new Dictionary<string, object>
        {
            ["Availability"] = snapshot.Availability.ToString(),
            ["PendingCount"] = outbox?.PendingCount ?? 0,
            ["PendingBytes"] = outbox?.PendingBytes ?? 0,
            ["LeasedCount"] = outbox?.LeasedCount ?? 0,
            ["RetryCount"] = outbox?.RetryCount ?? 0,
            ["QuarantineCount"] = outbox?.QuarantineCount ?? 0,
            ["TerminalCount"] = outbox?.TerminalCount ?? 0,
            ["OverflowCount"] = outbox?.OverflowCount ?? 0,
            ["OldestAgeSeconds"] = outbox?.OldestPendingUtc is { } oldest
                ? Math.Max(0, (timeProvider.GetUtcNow() - oldest).TotalSeconds)
                : 0
        };
        return Task.FromResult(snapshot.Availability switch
        {
            EnvironmentalObservationDeliveryAvailability.Healthy =>
                HealthCheckResult.Healthy("Environmental observation delivery is current.", data),
            EnvironmentalObservationDeliveryAvailability.Unhealthy =>
                HealthCheckResult.Unhealthy("Environmental observation delivery requires operator attention.", data: data),
            _ => HealthCheckResult.Degraded("Environmental observation delivery is initializing or retrying.", data: data)
        });
    }
}
