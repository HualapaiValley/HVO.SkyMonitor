using System.Data.Common;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

internal sealed class DeploymentLocationHealthCheck(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    DeploymentLocationTelemetry telemetry,
    IOptions<DeploymentLocationReconciliationOptions>? options = null) : IHealthCheck
{
    private const string OrdinalCollation = "Latin1_General_100_BIN2";

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
            var now = timeProvider.GetUtcNow();
            var work = await dbContext.DeploymentLocationReconciliationWork.AsNoTracking()
                .Where(item => item.Status != DeploymentLocationReconciliationStatuses.Completed)
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    Count = group.LongCount(),
                    Undiscovered = group.LongCount(item => item.CaptureCount == null),
                    PendingCaptures = group.Sum(item => item.CaptureCount == null
                        ? item.DiscoveredCaptureCount
                        : item.CaptureCount.Value - item.CompletedCaptureCount),
                    Retry = group.LongCount(item => item.Status == DeploymentLocationReconciliationStatuses.Retry),
                    Expired = group.LongCount(item => item.Status == DeploymentLocationReconciliationStatuses.Processing
                        && item.LeaseExpiresAtUtc <= now),
                    OldestCreated = group.Min(item => item.CreatedAtUtc),
                    OldestProgress = group.Min(item => item.UpdatedAtUtc)
                }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var resolvedFrames = dbContext.CentralFrames.AsNoTracking().Where(item =>
                item.LocationEvidenceState == CentralCaptureLocationEvidenceState.ReportedResolved);
            var ordinalInvariantFailure = dbContext.Database.IsRelational()
                ? await resolvedFrames.AnyAsync(item => item.Location != null
                    && item.Location.DeploymentLocation != null
                    && (EF.Functions.Collate(item.Location.LocationId, OrdinalCollation)
                            != EF.Functions.Collate(item.Location.DeploymentLocation.LocationId, OrdinalCollation)
                        || EF.Functions.Collate(item.Location.Source, OrdinalCollation)
                            != EF.Functions.Collate(item.Location.DeploymentLocation.Source, OrdinalCollation)),
                    cancellationToken).ConfigureAwait(false)
                : await resolvedFrames.AnyAsync(item => item.Location != null
                    && item.Location.DeploymentLocation != null
                    && (item.Location.LocationId != item.Location.DeploymentLocation.LocationId
                        || item.Location.Source != item.Location.DeploymentLocation.Source),
                    cancellationToken).ConfigureAwait(false);
            var invariantFailure = ordinalInvariantFailure
                || await resolvedFrames.AnyAsync(item => item.Location == null
                    || item.Location.DeviceDeploymentLocationVersionId == null
                    || item.Location.DeploymentLocation == null
                    || item.Location.DeploymentLocation.Status != DeploymentLocationResolutionStatus.Acknowledged
                    || item.Location.DeploymentLocation.ObservatoryLocationVersion == null
                    || item.Location.Version != item.Location.DeploymentLocation.Version
                    || item.Location.HorizontalAccuracyMeters != item.Location.DeploymentLocation.HorizontalAccuracyMeters
                    || item.Location.EffectiveFromUtc != item.Location.DeploymentLocation.EffectiveFromUtc
                    || item.Location.EffectiveUntilUtc != item.Location.DeploymentLocation.EffectiveUntilUtc
                    || item.CapturedAtUtc < item.Location.DeploymentLocation.ObservatoryLocationVersion.EffectiveFromUtc
                    || item.Location.DeploymentLocation.ObservatoryLocationVersion.SupersededAtUtc != null
                        && item.CapturedAtUtc >= item.Location.DeploymentLocation.ObservatoryLocationVersion.SupersededAtUtc
                    || item.CapturedAtUtc < item.Location.DeploymentLocation.EffectiveFromUtc
                    || item.Location.DeploymentLocation.EffectiveUntilUtc != null
                        && item.CapturedAtUtc >= item.Location.DeploymentLocation.EffectiveUntilUtc,
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
                ? Math.Max(0, (now - oldest.Value).TotalSeconds)
                : 0;
            var oldestWorkAgeSeconds = work is null
                ? 0
                : Math.Max(0, (now - work.OldestCreated).TotalSeconds);
            var oldestWorkProgressAgeSeconds = work is null
                ? 0
                : Math.Max(0, (now - work.OldestProgress).TotalSeconds);
            telemetry.RecordPendingSnapshot(pendingCount, oldestAgeSeconds);
            var data = new Dictionary<string, object>
            {
                ["PendingCount"] = pendingCount,
                ["OldestAgeSeconds"] = oldestAgeSeconds,
                ["PendingWorkCount"] = work?.Count ?? 0,
                ["PendingCaptureCount"] = work?.PendingCaptures ?? 0,
                ["OldestWorkAgeSeconds"] = oldestWorkAgeSeconds,
                ["OldestWorkProgressAgeSeconds"] = oldestWorkProgressAgeSeconds,
                ["UndiscoveredWorkCount"] = work?.Undiscovered ?? 0,
                ["RetryCount"] = work?.Retry ?? 0,
                ["ExpiredLeaseCount"] = work?.Expired ?? 0
            };
            if (invariantFailure)
            {
                return HealthCheckResult.Unhealthy("Deployment-location persistence invariants failed.", data: data);
            }
            var staleWork = work is not null && (work.Retry > 0 || work.Expired > 0
                || now - work.OldestProgress >= (options?.Value.BacklogDegradedAfter ?? TimeSpan.FromMinutes(10)));
            return pendingCount > 0 || staleWork
                ? HealthCheckResult.Degraded("Deployment-location reconciliation requires operator attention.", data: data)
                : HealthCheckResult.Healthy("Deployment-location reconciliation is current.", data);
        }
        catch (Exception exception) when (exception is DbException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("Deployment-location state could not be evaluated.");
        }
    }
}
