using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

/// <summary>
/// Reports runner registry state against the host placement policy. Runner-placed work with no active runner is a
/// backlog condition by design (no in-process fallback), so it degrades rather than fails.
/// </summary>
internal sealed class CentralProcessingRunnerHealthCheck(
    ApplicationDbContext dbContext,
    ICentralProcessingRunnerRegistry registry,
    IOptions<CentralProcessingRunnerOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return HealthCheckResult.Healthy("Processing runners are disabled.", new Dictionary<string, object>
            {
                ["enabled"] = false
            });
        }
        var counts = await registry.RefreshStatusesAsync(cancellationToken).ConfigureAwait(false);
        var placed = settings.ResolveRunnerPlacedRecipes();
        var now = timeProvider.GetUtcNow();
        var pendingStatuses = new[] { CentralDerivativeJobStatus.Pending, CentralDerivativeJobStatus.RetryableFailure };
        var backlog = placed.Count == 0
            ? null
            : await dbContext.CentralDerivativeJobs.AsNoTracking()
                .Where(job => pendingStatuses.Contains(job.Status)
                    && placed.Contains(job.RecipeName)
                    && job.AvailableAtUtc != null
                    && job.AvailableAtUtc <= now)
                .GroupBy(_ => 1)
                .Select(group => new { Count = group.LongCount(), Oldest = group.Min(job => job.AvailableAtUtc!.Value) })
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var pending = backlog?.Count ?? 0;
        var oldestAge = backlog is null ? 0 : Math.Max(0, (long)(now - backlog.Oldest).TotalSeconds);
        var active = counts[CentralProcessingRunnerStatus.Active];
        var data = new Dictionary<string, object>
        {
            ["enabled"] = true,
            ["activeRunners"] = active,
            ["staleRunners"] = counts[CentralProcessingRunnerStatus.Stale],
            ["retiredRunners"] = counts[CentralProcessingRunnerStatus.Retired],
            ["runnerPlacedRecipes"] = placed.Count,
            ["pendingRunnerJobs"] = pending,
            ["oldestPendingRunnerJobAgeSeconds"] = oldestAge
        };
        if (placed.Count != 0 && active == 0)
        {
            return HealthCheckResult.Degraded(
                "Recipes are placed on runners but no runner is active; runner-placed work is backlogged.", data: data);
        }
        if (pending > 0 && oldestAge > settings.BacklogDegradedAfter.TotalSeconds)
        {
            return HealthCheckResult.Degraded("Runner-placed work is older than the backlog threshold.", data: data);
        }
        return HealthCheckResult.Healthy("Processing runners are serving placed recipes.", data);
    }
}
