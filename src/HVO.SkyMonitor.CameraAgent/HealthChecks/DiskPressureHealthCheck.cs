using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.HealthChecks;

public sealed class DiskPressureHealthCheck(StoragePressureState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshots = state.Snapshots;
        if (snapshots.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded("Storage capacity has not been evaluated."));
        }
        var failed = snapshots.FirstOrDefault(snapshot => snapshot.ProbeFailure is not null);
        if (failed is not null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Capacity probe failed for {failed.StorageRoot}: {failed.ProbeFailure}"));
        }
        var pressured = snapshots.FirstOrDefault(snapshot => snapshot.IsUnderPressure);
        return Task.FromResult(pressured is null
            ? HealthCheckResult.Healthy("Storage capacity is above configured pressure thresholds.")
            : HealthCheckResult.Degraded(
                $"Disk pressure at {pressured.StorageRoot}: {pressured.Capacity.AvailablePercent:F2}% available; effective retention {pressured.EffectiveRetentionDays} day(s)."));
    }
}
