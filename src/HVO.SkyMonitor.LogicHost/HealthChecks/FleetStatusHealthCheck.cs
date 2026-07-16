using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

internal sealed class FleetStatusHealthCheck(
    ApplicationDbContext dbContext,
    FleetRetentionState retentionState,
    TimeProvider timeProvider,
    IOptions<FleetStatusOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var freshCutoff = now.AddSeconds(-options.Value.FreshSeconds);
        var offlineCutoff = now.AddSeconds(-options.Value.OfflineSeconds);
        var continuityCutoff = offlineCutoff;
        var query =
            from registration in dbContext.DeviceRegistrations.AsNoTracking()
            where registration.Status == DeviceRegistrationStatus.Active
            join fleetState in dbContext.DeviceFleetStates.AsNoTracking()
                on registration.Id equals fleetState.RegistrationId into fleetStates
            from state in fleetStates.DefaultIfEmpty()
            select new { registration, state };
        var counts = await query
            .GroupBy(static _ => 1)
            .Select(group => new
            {
                Total = group.Count(),
                Offline = group.Count(item =>
                    item.state == null
                        ? item.registration.ActivatedAtUtc == null || item.registration.ActivatedAtUtc <= offlineCutoff
                        : item.state.ReceivedAtUtc <= offlineCutoff),
                Online = group.Count(item =>
                    item.state != null &&
                    item.state.ReceivedAtUtc > freshCutoff &&
                    item.state.ReportedHealth == HVO.SkyMonitor.Fleet.Contracts.FleetHealth.Healthy &&
                    !item.state.HasStoragePressure &&
                    !item.state.HasRequiredLaneFailure &&
                    !item.state.HasQuarantine &&
                    item.state.ClockDiagnostic == FleetClockDiagnostic.WithinTolerance &&
                    (item.state.LastSequenceGapUtc == null || item.state.LastSequenceGapUtc <= continuityCutoff) &&
                    (item.state.LastBootSessionChangeUtc == null || item.state.LastBootSessionChangeUtc <= continuityCutoff))
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var total = counts?.Total ?? 0;
        var offline = counts?.Offline ?? 0;
        var online = counts?.Online ?? 0;
        var degraded = total - offline - online;
        var data = new Dictionary<string, object>
        {
            ["OnlineCount"] = online,
            ["DegradedCount"] = degraded,
            ["OfflineCount"] = offline,
            ["RetentionLastSucceededUtc"] = retentionState.Snapshot.LastSucceededUtc?.ToString("O") ?? "never"
        };
        var retention = retentionState.Snapshot;
        if (retention.FailureReason is not null)
        {
            return HealthCheckResult.Unhealthy("Fleet status retention is failing.", data: data);
        }
        if (retention.LastSucceededUtc is null)
        {
            return HealthCheckResult.Degraded("Fleet status retention has not completed its first sweep.", data: data);
        }
        return now - retention.LastSucceededUtc > TimeSpan.FromHours(2)
            ? HealthCheckResult.Unhealthy("Fleet status retention has not completed a recent sweep.", data: data)
            : HealthCheckResult.Healthy("Fleet status persistence and retention are operational.", data);
    }
}
