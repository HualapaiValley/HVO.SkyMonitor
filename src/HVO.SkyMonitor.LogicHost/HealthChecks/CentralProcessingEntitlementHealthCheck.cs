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
        // Grouped by observatory, camera, and recipe so every enforced dimension (observatory, camera, class, and
        // observatory-class) can be evaluated from one query; the recipe maps to its resource class in memory.
        var groups = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(job => pendingStatuses.Contains(job.Status) || job.Status == CentralDerivativeJobStatus.Leased)
            .GroupBy(job => new { job.SourceArtifact!.Frame!.ObservatoryId, job.SourceArtifact.Frame.DevicePublicId, job.RecipeName })
            .Select(group => new
            {
                group.Key.ObservatoryId,
                group.Key.DevicePublicId,
                group.Key.RecipeName,
                Pending = group.LongCount(job => pendingStatuses.Contains(job.Status)),
                Leased = group.LongCount(job => job.Status == CentralDerivativeJobStatus.Leased && job.LeaseExpiresAtUtc > now),
                Oldest = group.Where(job => pendingStatuses.Contains(job.Status) && job.AvailableAtUtc != null)
                    .Min(job => job.AvailableAtUtc)
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var leasedBytesByRecipe = await dbContext.CentralDerivativeJobInputs.AsNoTracking()
            .Where(input => input.Job!.Status == CentralDerivativeJobStatus.Leased && input.Job.LeaseExpiresAtUtc > now)
            .GroupBy(input => input.Job!.RecipeName)
            .Select(group => new { RecipeName = group.Key, Bytes = group.Sum(input => input.ByteLength) })
            .ToDictionaryAsync(item => item.RecipeName, item => item.Bytes, cancellationToken).ConfigureAwait(false);
        var leasedBytesByClass = leasedBytesByRecipe
            .GroupBy(pair => settings.ResolveResourceClass(pair.Key))
            .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value));
        var threshold = settings.BacklogDegradedAfter;
        bool OldBacklog(DateTimeOffset? oldest) => oldest is { } value && now - value > threshold;
        var byObservatory = groups.GroupBy(item => item.ObservatoryId).ToList();
        var byClass = groups.GroupBy(item => settings.ResolveResourceClass(item.RecipeName))
            .ToDictionary(group => group.Key, group => (Leased: group.Sum(item => item.Leased), OldBacklog: group.Any(item => OldBacklog(item.Oldest))));
        var overAdmission = new List<string>();
        var saturated = new List<string>();
        var saturatedDimensions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var observatory in byObservatory)
        {
            var id = observatory.Key;
            var pending = observatory.Sum(item => item.Pending);
            var leased = observatory.Sum(item => item.Leased);
            var oldBacklog = observatory.Any(item => OldBacklog(item.Oldest));
            if (settings.AdmissionPendingLimit > 0 && pending > settings.AdmissionPendingLimit)
            {
                overAdmission.Add(id.ToString("D"));
            }
            var dimensions = new List<string>();
            var entitlement = settings.ResolveActiveJobs(id);
            if (entitlement > 0 && leased >= entitlement && oldBacklog)
            {
                dimensions.Add("observatory");
            }
            var cameraLimit = settings.ResolveActiveJobsPerCamera(id);
            if (cameraLimit > 0 && observatory.GroupBy(item => item.DevicePublicId)
                    .Any(camera => camera.Sum(item => item.Leased) >= cameraLimit && camera.Any(item => OldBacklog(item.Oldest))))
            {
                dimensions.Add("camera");
            }
            var perClass = observatory.GroupBy(item => settings.ResolveResourceClass(item.RecipeName))
                .Select(group => (Class: group.Key, Leased: group.Sum(item => item.Leased), OldBacklog: group.Any(item => OldBacklog(item.Oldest))))
                .ToList();
            var observatoryClassLimits = settings.Find(id)?.ResourceClassActiveJobs;
            if (perClass.Any(item => observatoryClassLimits is not null
                    && observatoryClassLimits.TryGetValue(item.Class, out var limit) && limit > 0 && item.Leased >= limit && item.OldBacklog))
            {
                dimensions.Add("observatory-class");
            }
            if (perClass.Any(item => settings.ResourceClasses.TryGetValue(item.Class, out var budget) && budget.ActiveJobs > 0
                    && byClass.TryGetValue(item.Class, out var classTotals) && classTotals.Leased >= budget.ActiveJobs && item.OldBacklog))
            {
                dimensions.Add("class");
            }
            if (perClass.Any(item => settings.ResourceClasses.TryGetValue(item.Class, out var budget) && budget.ActiveInputBytes > 0
                    && leasedBytesByClass.TryGetValue(item.Class, out var classBytes) && classBytes >= budget.ActiveInputBytes && item.OldBacklog))
            {
                dimensions.Add("class-bytes");
            }
            if (dimensions.Count != 0)
            {
                saturated.Add(id.ToString("D"));
                saturatedDimensions.UnionWith(dimensions);
            }
        }
        var data = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["observatoriesWithWork"] = byObservatory.Count,
            ["pendingJobs"] = groups.Sum(item => item.Pending),
            ["activeLeases"] = groups.Sum(item => item.Leased),
            ["overAdmissionLimit"] = overAdmission.Count,
            ["saturatedWithOldBacklog"] = saturated.Count,
            ["saturatedDimensions"] = string.Join(',', saturatedDimensions)
        };
        if (overAdmission.Count != 0)
        {
            return HealthCheckResult.Degraded(
                $"{overAdmission.Count} observatory(ies) exceed the admission pending limit; scheduling continues under entitlements.", data: data);
        }
        if (saturated.Count != 0)
        {
            return HealthCheckResult.Degraded(
                $"{saturated.Count} observatory(ies) are saturated at an entitlement ({string.Join(", ", saturatedDimensions)}) with backlog older than the threshold.", data: data);
        }
        return HealthCheckResult.Healthy("Processing entitlements are within limits.", data);
    }
}
