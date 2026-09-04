using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
using System.Text.Json;

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
        var threshold = settings.BacklogDegradedAfter;
        var oldPendingBefore = CentralProcessingEntitlementOptions.StarvationThreshold(now, threshold);
        // Byte budgets throttle a pending job whenever it does not fit the remaining capacity (the claim rejects
        // `active + candidate > budget`), so byte saturation is judged by the largest old pending job of each
        // budgeted class in each observatory; a supply of smaller work cannot mask a stranded larger job. Both
        // aggregates run server-side and only when some class carries a byte budget.
        var byteBudgetClasses = settings.ResourceClasses.Where(pair => pair.Value.ActiveInputBytes > 0).Select(pair => pair.Key).ToArray();
        var leasedBytesByClass = new Dictionary<string, long>(StringComparer.Ordinal);
        var largestOldPendingBytes = new Dictionary<(Guid ObservatoryId, string Class), long>();
        if (byteBudgetClasses.Length != 0)
        {
            var classes = new SqlParameter("@classes", System.Data.SqlDbType.NVarChar, -1) { Value = settings.CreateRecipeClassesJson() };
            var budgeted = new SqlParameter("@budgeted", System.Data.SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(byteBudgetClasses) };
            foreach (var row in await dbContext.Database.SqlQueryRaw<ClassBytesRow>("""
                    SELECT COALESCE(rc.[cls], N'image') AS [ResourceClass], COALESCE(SUM(a.[ByteLength]), 0) AS [Bytes]
                    FROM [CentralDerivativeJobs] AS job
                    INNER JOIN [CentralDerivativeJobInputs] AS i ON i.[CentralDerivativeJobId] = job.[Id]
                    INNER JOIN [CentralArtifacts] AS a ON a.[Id] = i.[CentralArtifactId]
                    LEFT JOIN OPENJSON(@classes) WITH ([r] nvarchar(128) '$.r', [cls] nvarchar(64) '$.cls') AS rc ON rc.[r] = job.[RecipeName]
                    WHERE job.[Status] = N'Leased' AND job.[LeaseExpiresAtUtc] > @now
                      AND COALESCE(rc.[cls], N'image') IN (SELECT [value] FROM OPENJSON(@budgeted))
                    GROUP BY COALESCE(rc.[cls], N'image')
                    """, classes, budgeted, new SqlParameter("@now", now))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                leasedBytesByClass[row.ResourceClass] = row.Bytes;
            }
            foreach (var row in await dbContext.Database.SqlQueryRaw<OldPendingBytesRow>("""
                    SELECT frame.[ObservatoryId] AS [ObservatoryId], COALESCE(rc.[cls], N'image') AS [ResourceClass], MAX(sized.[Bytes]) AS [Bytes]
                    FROM [CentralDerivativeJobs] AS job
                    INNER JOIN [CentralArtifacts] AS source ON source.[Id] = job.[SourceCentralArtifactId]
                    INNER JOIN [CentralFrames] AS frame ON frame.[Id] = source.[CentralFrameId]
                    LEFT JOIN OPENJSON(@classes) WITH ([r] nvarchar(128) '$.r', [cls] nvarchar(64) '$.cls') AS rc ON rc.[r] = job.[RecipeName]
                    CROSS APPLY (SELECT COALESCE(SUM(a.[ByteLength]), 0) AS [Bytes]
                                 FROM [CentralDerivativeJobInputs] AS i INNER JOIN [CentralArtifacts] AS a ON a.[Id] = i.[CentralArtifactId]
                                 WHERE i.[CentralDerivativeJobId] = job.[Id]) AS sized
                    WHERE job.[Status] IN (N'Pending', N'RetryableFailure') AND job.[AvailableAtUtc] < @before
                      AND COALESCE(rc.[cls], N'image') IN (SELECT [value] FROM OPENJSON(@budgeted))
                    GROUP BY frame.[ObservatoryId], COALESCE(rc.[cls], N'image')
                    """, new SqlParameter("@classes", System.Data.SqlDbType.NVarChar, -1) { Value = settings.CreateRecipeClassesJson() },
                    new SqlParameter("@budgeted", System.Data.SqlDbType.NVarChar, -1) { Value = JsonSerializer.Serialize(byteBudgetClasses) },
                    new SqlParameter("@before", oldPendingBefore))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                largestOldPendingBytes[(row.ObservatoryId, row.ResourceClass)] = row.Bytes;
            }
        }
        bool OldBacklog(DateTimeOffset? oldest) => oldest is { } value && value < oldPendingBefore;
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
                    && largestOldPendingBytes.TryGetValue((id, item.Class), out var largestOldPending)
                    && (leasedBytesByClass.TryGetValue(item.Class, out var classBytes) ? classBytes : 0) + largestOldPending > budget.ActiveInputBytes))
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

    private sealed record ClassBytesRow(string ResourceClass, long Bytes);

    private sealed record OldPendingBytesRow(Guid ObservatoryId, string ResourceClass, long Bytes);
}
