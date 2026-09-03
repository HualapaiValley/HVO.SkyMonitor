using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

internal sealed class ProcessingGraphDeliveryHealthCheck(
    ProcessingGraphDeliveryState state,
    TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = state.Snapshot;
        var data = new Dictionary<string, object>
        {
            ["reasonCode"] = snapshot.ReasonCode,
            ["pendingProposalCount"] = snapshot.PendingProposalCount,
            ["pendingFactCount"] = snapshot.PendingFactCount,
            ["oldestPendingProposalAgeSeconds"] = snapshot.OldestPendingProposalUtc is { } oldestProposal
                ? Math.Max(0, (timeProvider.GetUtcNow() - oldestProposal).TotalSeconds)
                : 0,
            ["oldestPendingFactAgeSeconds"] = snapshot.OldestPendingFactUtc is { } oldestFact
                ? Math.Max(0, (timeProvider.GetUtcNow() - oldestFact).TotalSeconds)
                : 0,
            ["lastCentralContactUtc"] = snapshot.LastCentralContactUtc?.ToString("O") ?? "never"
        };
        return Task.FromResult(snapshot.Availability switch
        {
            ProcessingGraphDeliveryAvailability.Healthy or ProcessingGraphDeliveryAvailability.Disabled =>
                HealthCheckResult.Healthy(snapshot.ReasonCode, data),
            ProcessingGraphDeliveryAvailability.Initializing or ProcessingGraphDeliveryAvailability.Degraded =>
                HealthCheckResult.Degraded(snapshot.ReasonCode, data: data),
            _ => HealthCheckResult.Unhealthy(snapshot.ReasonCode, data: data)
        });
    }
}
