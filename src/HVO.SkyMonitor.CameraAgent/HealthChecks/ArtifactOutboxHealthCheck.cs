using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

public sealed class ArtifactOutboxHealthCheck(
    ArtifactOutboxState state,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = state.Snapshot;
        var oldestAge = snapshot.OldestPendingUtc is { } oldest
            ? timeProvider.GetUtcNow() - oldest
            : TimeSpan.Zero;
        var thresholds = options.Value.CaptureDistribution;
        var exceedsThreshold = snapshot.PendingCount > thresholds.RequiredMaximumPendingCount
            || snapshot.PendingBytes > thresholds.RequiredMaximumPendingBytes
            || oldestAge > TimeSpan.FromMinutes(thresholds.RequiredMaximumOldestAgeMinutes);
        var data = new Dictionary<string, object>
        {
            ["Availability"] = snapshot.Availability.ToString(),
            ["PendingCount"] = snapshot.PendingCount,
            ["PendingBytes"] = snapshot.PendingBytes,
            ["LeasedCount"] = snapshot.LeasedCount,
            ["RetryCount"] = snapshot.RetryCount,
            ["QuarantineCount"] = snapshot.QuarantineCount,
            ["OldestAgeSeconds"] = Math.Max(0, oldestAge.TotalSeconds)
        };
        var result = (snapshot.Availability, exceedsThreshold) switch
        {
            (ArtifactOutboxAvailability.Healthy, false) => HealthCheckResult.Healthy("Artifact outbox is draining durable work.", data),
            (ArtifactOutboxAvailability.Healthy, true) => HealthCheckResult.Degraded("Artifact outbox backlog exceeds its configured threshold.", data: data),
            (ArtifactOutboxAvailability.Degraded, _) => HealthCheckResult.Degraded("Artifact outbox has retries or quarantined evidence.", data: data),
            (ArtifactOutboxAvailability.Initializing, _) => HealthCheckResult.Degraded("Artifact outbox has not completed initialization.", data: data),
            _ => HealthCheckResult.Unhealthy("Artifact outbox journal is unavailable.", data: data)
        };
        return Task.FromResult(result);
    }
}
