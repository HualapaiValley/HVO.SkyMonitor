using HVO.SkyMonitor.CameraAgent.Common.Capture;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

public sealed class CaptureAdmissionHealthCheck(CaptureAdmissionCoordinator coordinator) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = coordinator.Snapshot;
        var data = new Dictionary<string, object>
        {
            ["State"] = snapshot.State.ToString(),
            ["Version"] = snapshot.Version,
            ["IsInitialized"] = snapshot.IsInitialized
        };
        return Task.FromResult(snapshot.State switch
        {
            CaptureAdmissionState.Running => HealthCheckResult.Healthy("Capture admission is running.", data),
            CaptureAdmissionState.Paused => HealthCheckResult.Healthy("Capture admission is intentionally paused.", data),
            CaptureAdmissionState.PauseRequested => HealthCheckResult.Degraded("Capture admission is draining an in-flight capture.", data: data),
            CaptureAdmissionState.Initializing => HealthCheckResult.Degraded("Capture admission has not initialized.", data: data),
            _ => HealthCheckResult.Unhealthy("Capture admission durable state is unavailable.", data: data)
        });
    }
}
