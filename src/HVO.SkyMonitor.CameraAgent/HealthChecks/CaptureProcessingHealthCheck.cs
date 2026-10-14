using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

public sealed class CaptureProcessingHealthCheck(CaptureProcessingState state) : IHealthCheck
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
            ["RetryCount"] = snapshot.RetryCount,
            ["TerminalCount"] = snapshot.TerminalCount,
            ["ProcessingQuarantineCount"] = snapshot.ProcessingQuarantineCount,
            ["MissingProductCount"] = snapshot.MissingProductCount,
            ["DurableStateUnavailable"] = snapshot.DurableStateUnavailable,
            ["ReconciliationFailed"] = snapshot.ReconciliationFailed,
            ["ReplayPendingCount"] = snapshot.ReplayPendingCount,
            ["ReplayRetryCount"] = snapshot.ReplayRetryCount,
            ["ReplayTerminalCount"] = snapshot.ReplayTerminalCount,
            ["OldestReplayPendingAgeSeconds"] = snapshot.OldestReplayPendingUtc is null
                ? 0
                : Math.Max(0, (DateTimeOffset.UtcNow - snapshot.OldestReplayPendingUtc.Value).TotalSeconds),
            ["OldestPendingAgeSeconds"] = snapshot.OldestPendingUtc is null
                ? 0
                : Math.Max(0, (DateTimeOffset.UtcNow - snapshot.OldestPendingUtc.Value).TotalSeconds),
            ["Reason"] = snapshot.Reason
        };
        if (snapshot.SupersededActiveRevisionId is { } supersededRevision)
        {
            data["SupersededActiveRevisionId"] = supersededRevision;
        }
        var result = snapshot.Availability switch
        {
            CaptureProcessingAvailability.Healthy => HealthCheckResult.Healthy(
                "Capture processing is completing durable graphs.", data),
            CaptureProcessingAvailability.Degraded => HealthCheckResult.Degraded(
                snapshot.SupersededActiveRevisionId is null
                    ? "Capture processing has retryable graph work."
                    : "The active processing graph revision was compiled by an earlier version; activate a compatible revision or roll back to configured-basic.",
                data: data),
            _ => HealthCheckResult.Unhealthy(
                "Capture processing has a required terminal graph outcome.", data: data)
        };
        return Task.FromResult(result);
    }
}
