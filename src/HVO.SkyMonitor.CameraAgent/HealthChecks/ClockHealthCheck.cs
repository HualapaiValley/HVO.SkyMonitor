using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

/// <summary>
/// Reports the latest clock measurement. It is never unhealthy: a clock problem must not stop capture, so it is
/// reported as degraded and recorded instead.
/// </summary>
public sealed class ClockHealthCheck(IClockSyncMonitor monitor) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var assessment = ClockAssessment.Evaluate(monitor.Settings, monitor.Latest);
        var data = new Dictionary<string, object>
        {
            ["Availability"] = assessment.Status == ClockSyncStatus.Disabled ? "Disabled" : "Enabled",
            ["Status"] = assessment.Status.ToString()
        };
        return Task.FromResult(assessment.IsHealthy
            ? HealthCheckResult.Healthy(assessment.Description, data)
            : HealthCheckResult.Degraded(assessment.Description, data: data));
    }
}
