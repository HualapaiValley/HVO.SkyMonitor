using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

public sealed class EnvironmentalObservationDeliveryHealthCheck(
    EnvironmentalObservationDeliveryState state,
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
                "Environmental delivery is disabled/not configured.",
                new Dictionary<string, object>
                {
                    ["Availability"] = "Disabled",
                    ["PendingCount"] = 0L,
                    ["PendingBytes"] = 0L,
                    ["LeasedCount"] = 0L,
                    ["RetryCount"] = 0L,
                    ["QuarantineCount"] = 0L,
                    ["TerminalCount"] = 0L,
                    ["OverflowCount"] = 0L,
                    ["OldestAgeSeconds"] = 0D
                }));
        }

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
