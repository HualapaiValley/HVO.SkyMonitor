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
            ["LaneQuarantineTerminalCount"] = snapshot.LaneQuarantineTerminalCount,
            ["ProcessingQuarantineCount"] = snapshot.ProcessingQuarantineCount,
            ["MissingProductCount"] = snapshot.MissingProductCount,
            ["DurableStateUnavailable"] = snapshot.DurableStateUnavailable,
            ["ReconciliationFailed"] = snapshot.ReconciliationFailed,
            ["ReplayPendingCount"] = snapshot.ReplayPendingCount,
            ["ReplayRetryCount"] = snapshot.ReplayRetryCount,
            ["ReplayTerminalCount"] = snapshot.ReplayTerminalCount,
            ["ReplayAbandonedCount"] = snapshot.ReplayAbandonedCount,
            ["StoragePolicySkippedCount"] = snapshot.StoragePolicySkippedCount,
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
        if (snapshot.StoragePolicyMismatchActiveRevisionId is { } mismatchedRevision)
        {
            data["StoragePolicyMismatchActiveRevisionId"] = mismatchedRevision;
        }
        var result = snapshot.Availability switch
        {
            CaptureProcessingAvailability.Healthy => HealthCheckResult.Healthy(
                "Capture processing is completing durable graphs.", data),
            CaptureProcessingAvailability.Degraded => HealthCheckResult.Degraded(
                snapshot.StoragePolicyMismatchActiveRevisionId is not null
                    ? "The active processing graph Storage settings differ from the capture profile; reconcile and activate a matching graph. Raw is retained while other standard processing is skipped; inspect execution reasons for unavailable optional scene evidence."
                    : snapshot.StoragePolicySkippedCount > 0
                    ? "Standard work was skipped after a Storage profile change in the past 24 hours. Inspect Operations → Pipeline → Executions and explicitly replay still-retained inputs if needed; new compatible work can continue."
                    : snapshot.SupersededActiveRevisionId is null
                    ? "Capture processing has retryable graph work."
                    : "The active processing graph revision was compiled by an earlier version; activate a compatible revision or roll back to configured-basic.",
                data: data),
            _ => HealthCheckResult.Unhealthy(
                "Capture processing has a required terminal graph outcome.", data: data)
        };
        return Task.FromResult(result);
    }
}
