using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.Fleet.Contracts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

public sealed class FleetHeartbeatHealthCheck(
    FleetHeartbeatState state,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (options.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy(
                "Fleet heartbeat is disabled/not configured.",
                new Dictionary<string, object>
                {
                    ["Availability"] = "Disabled",
                    ["PendingCount"] = 0L,
                    ["PendingBytes"] = 0L,
                    ["RetryCount"] = 0L,
                    ["QuarantineCount"] = 0L,
                    ["OverflowCount"] = 0L,
                    ["BlockedCount"] = 0L,
                    ["OldestAgeSeconds"] = 0D
                }));
        }

        var snapshot = state.Snapshot;
        var outbox = snapshot.Outbox;
        var oldestAge = outbox?.OldestPendingUtc is { } oldest
            ? Math.Max(0, (timeProvider.GetUtcNow() - oldest).TotalSeconds)
            : 0;
        var data = new Dictionary<string, object>
        {
            ["Availability"] = snapshot.Availability.ToString(),
            ["PendingCount"] = outbox?.PendingCount ?? 0,
            ["PendingBytes"] = outbox?.PendingBytes ?? 0,
            ["RetryCount"] = outbox?.RetryCount ?? 0,
            ["QuarantineCount"] = outbox?.QuarantineCount ?? 0,
            ["OverflowCount"] = outbox?.OverflowCount ?? 0,
            ["BlockedCount"] = outbox?.BlockedCount ?? 0,
            ["OldestAgeSeconds"] = oldestAge
        };
        return Task.FromResult(snapshot.Availability switch
        {
            FleetAvailability.Available => HealthCheckResult.Healthy("Fleet heartbeat delivery is current.", data),
            FleetAvailability.Unavailable => HealthCheckResult.Unhealthy("Fleet heartbeat delivery has quarantined or unavailable durable state.", data: data),
            _ => HealthCheckResult.Degraded("Fleet heartbeat delivery is initializing or retrying.", data: data)
        });
    }
}
