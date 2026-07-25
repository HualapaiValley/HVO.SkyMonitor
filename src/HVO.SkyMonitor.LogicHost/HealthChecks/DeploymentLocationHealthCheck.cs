using System.Data.Common;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

internal sealed class DeploymentLocationHealthCheck(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    DeploymentLocationTelemetry telemetry) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pending = await dbContext.DeviceDeploymentLocationVersions.AsNoTracking()
                .Where(item => item.Status == DeploymentLocationResolutionStatus.Pending)
                .GroupBy(_ => 1)
                .Select(group => new { Count = group.LongCount(), Oldest = group.Min(item => item.ProposedAtUtc) })
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var mismatches = await dbContext.CentralFrames.AsNoTracking()
                .Where(item => item.LocationEvidenceState == CentralCaptureLocationEvidenceState.ReportedUnresolved
                    || item.LocationEvidenceState == CentralCaptureLocationEvidenceState.Mismatch)
                .GroupBy(_ => 1)
                .Select(group => new { Count = group.LongCount(), Oldest = group.Min(item => item.FirstReceivedAtUtc) })
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var invariantFailure = await dbContext.CentralFrames.AsNoTracking().AnyAsync(item =>
                item.LocationEvidenceState == CentralCaptureLocationEvidenceState.ReportedResolved
                && (item.Location == null
                    || item.Location.DeviceDeploymentLocationVersionId == null
                    || item.Location.DeploymentLocation == null
                    || item.Location.DeploymentLocation.Status != DeploymentLocationResolutionStatus.Acknowledged
                    || item.Location.LocationId != item.Location.DeploymentLocation.LocationId
                    || item.Location.Version != item.Location.DeploymentLocation.Version
                    || item.Location.Source != item.Location.DeploymentLocation.Source
                    || item.Location.HorizontalAccuracyMeters != item.Location.DeploymentLocation.HorizontalAccuracyMeters
                    || item.Location.EffectiveFromUtc != item.Location.DeploymentLocation.EffectiveFromUtc
                    || item.Location.EffectiveUntilUtc != item.Location.DeploymentLocation.EffectiveUntilUtc
                    || item.CapturedAtUtc < item.Location.DeploymentLocation.EffectiveFromUtc
                    || item.Location.DeploymentLocation.EffectiveUntilUtc != null
                        && item.CapturedAtUtc >= item.Location.DeploymentLocation.EffectiveUntilUtc),
                cancellationToken).ConfigureAwait(false)
                || await dbContext.Observatories.AsNoTracking().AnyAsync(item =>
                    item.CurrentLocationVersion == null
                    || item.CurrentLocationCanonicalSha256 == null
                    || item.LocationVersions.Count(version => version.SupersededAtUtc == null) != 1
                    || !item.LocationVersions.Any(version => version.Version == item.CurrentLocationVersion
                        && version.CanonicalSha256 == item.CurrentLocationCanonicalSha256
                        && version.SupersededAtUtc == null), cancellationToken).ConfigureAwait(false);
            var pendingCount = (pending?.Count ?? 0) + (mismatches?.Count ?? 0);
            var oldest = new[] { pending?.Oldest, mismatches?.Oldest }
                .Where(item => item.HasValue)
                .Min();
            var oldestAgeSeconds = oldest.HasValue
                ? Math.Max(0, (timeProvider.GetUtcNow() - oldest.Value).TotalSeconds)
                : 0;
            telemetry.RecordPendingSnapshot(pendingCount, oldestAgeSeconds);
            var data = new Dictionary<string, object>
            {
                ["PendingCount"] = pendingCount,
                ["OldestAgeSeconds"] = oldestAgeSeconds
            };
            if (invariantFailure)
            {
                return HealthCheckResult.Unhealthy("Deployment-location persistence invariants failed.", data: data);
            }
            return pendingCount > 0
                ? HealthCheckResult.Degraded("Deployment-location reconciliation requires operator attention.", data: data)
                : HealthCheckResult.Healthy("Deployment-location reconciliation is current.", data);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("Deployment-location state could not be evaluated.");
        }
    }
}
