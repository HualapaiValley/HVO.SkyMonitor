using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

/// <summary>
/// Admission and backpressure signal for fair scheduling (#429): raw ingest is never refused and scheduling continues,
/// so sustained overload shows up here as observatories over the admission pending limit or saturated with old work.
/// </summary>
internal sealed class CentralProcessingEntitlementHealthCheck(
    ApplicationDbContext dbContext,
    IOptions<CentralProcessingEntitlementOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return HealthCheckResult.Healthy("Processing entitlements are disabled.", new Dictionary<string, object>
            {
                ["enabled"] = false
            });
        }
        var now = timeProvider.GetUtcNow();
        var pendingStatuses = new[] { CentralDerivativeJobStatus.Pending, CentralDerivativeJobStatus.RetryableFailure };
        var observatories = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(job => pendingStatuses.Contains(job.Status) || job.Status == CentralDerivativeJobStatus.Leased)
            .GroupBy(job => job.SourceArtifact!.Frame!.ObservatoryId)
            .Select(group => new
            {
                ObservatoryId = group.Key,
                Pending = group.LongCount(job => pendingStatuses.Contains(job.Status)),
                Leased = group.LongCount(job => job.Status == CentralDerivativeJobStatus.Leased && job.LeaseExpiresAtUtc > now),
                Oldest = group.Where(job => pendingStatuses.Contains(job.Status) && job.AvailableAtUtc != null)
                    .Min(job => job.AvailableAtUtc)
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var overAdmission = new List<string>();
        var saturated = new List<string>();
        foreach (var observatory in observatories)
        {
            var entitlement = settings.ResolveActiveJobs(observatory.ObservatoryId);
            var oldestAge = observatory.Oldest is { } oldest ? Math.Max(0, (now - oldest).TotalSeconds) : 0;
            if (settings.AdmissionPendingLimit > 0 && observatory.Pending > settings.AdmissionPendingLimit)
            {
                overAdmission.Add(observatory.ObservatoryId.ToString("D"));
            }
            if (entitlement > 0 && observatory.Leased >= entitlement && oldestAge > settings.BacklogDegradedAfter.TotalSeconds)
            {
                saturated.Add(observatory.ObservatoryId.ToString("D"));
            }
        }
        var data = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["observatoriesWithWork"] = observatories.Count,
            ["pendingJobs"] = observatories.Sum(item => item.Pending),
            ["activeLeases"] = observatories.Sum(item => item.Leased),
            ["overAdmissionLimit"] = overAdmission.Count,
            ["saturatedWithOldBacklog"] = saturated.Count
        };
        if (overAdmission.Count != 0)
        {
            return HealthCheckResult.Degraded(
                $"{overAdmission.Count} observatory(ies) exceed the admission pending limit; scheduling continues under entitlements.", data: data);
        }
        if (saturated.Count != 0)
        {
            return HealthCheckResult.Degraded(
                $"{saturated.Count} observatory(ies) are saturated at their entitlement with backlog older than the threshold.", data: data);
        }
        return HealthCheckResult.Healthy("Processing entitlements are within limits.", data);
    }
}
