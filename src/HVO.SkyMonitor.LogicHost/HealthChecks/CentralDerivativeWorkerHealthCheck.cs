using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

internal sealed class CentralDerivativeWorkerHealthCheck(
    ApplicationDbContext dbContext,
    IOptions<CentralDerivativeWorkerOptions> options,
    CentralDerivativeWorkerTelemetry telemetry,
    TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return HealthCheckResult.Healthy(
                "Central derivative worker is disabled.",
                Data("disabled", 0, 0, 0, 0));
        }

        var now = timeProvider.GetUtcNow();
        var pendingStatuses = new[]
        {
            CentralDerivativeJobStatus.Pending,
            CentralDerivativeJobStatus.RetryableFailure
        };
        var snapshot = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(job => pendingStatuses.Contains(job.Status))
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.LongCount(),
                Oldest = group.Min(job => job.AvailableAtUtc ?? job.LeaseAcquiredAtUtc ?? job.CreatedAtUtc)
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var pendingCount = snapshot?.Count ?? 0;
        var oldestAge = snapshot is null ? 0 : Math.Max(0, (long)(now - snapshot.Oldest).TotalSeconds);
        var lastPollAge = AgeSeconds(now, telemetry.LastPollUtc);
        var lastSuccessAge = AgeSeconds(now, telemetry.LastSuccessUtc);
        var data = Data("healthy", telemetry.ActiveCount, pendingCount, oldestAge, lastSuccessAge);

        if (telemetry.LastPollUtc is null || lastPollAge > settings.LeaseDuration.TotalSeconds)
        {
            data["Status"] = "stale";
            return HealthCheckResult.Unhealthy("Central derivative worker heartbeat is stale.", data: data);
        }
        var hasRenewalFailure = telemetry.HasRecentRenewalFailure(now, settings.LeaseDuration);
        var hasDependencyFailure = telemetry.HasRecentDependencyFailure(now, settings.LeaseDuration);
        if (oldestAge > settings.BacklogDegradedAfter.TotalSeconds || hasRenewalFailure || hasDependencyFailure)
        {
            data["Status"] = hasRenewalFailure
                ? "renewal-failure"
                : hasDependencyFailure ? "dependency-failure" : "backlog";
            return HealthCheckResult.Degraded("Central derivative worker is degraded.", data: data);
        }
        return HealthCheckResult.Healthy("Central derivative worker is healthy.", data);
    }

    private static Dictionary<string, object> Data(
        string status,
        long activeSlots,
        long pendingCount,
        long oldestAgeSeconds,
        long lastSuccessAgeSeconds) => new()
        {
            ["Status"] = status,
            ["ActiveSlots"] = activeSlots,
            ["PendingCount"] = pendingCount,
            ["OldestAgeSeconds"] = oldestAgeSeconds,
            ["LastSuccessAgeSeconds"] = lastSuccessAgeSeconds
        };

    private static long AgeSeconds(DateTimeOffset now, DateTimeOffset? value)
        => value.HasValue ? Math.Max(0, (long)(now - value.Value).TotalSeconds) : 0;
}
