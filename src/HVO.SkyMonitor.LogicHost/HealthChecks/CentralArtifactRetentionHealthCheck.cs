using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

internal sealed class CentralArtifactRetentionHealthCheck(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<CentralArtifactRetentionOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var failed = await dbContext.CentralObjectRecoveryDispositions.AsNoTracking().AnyAsync(item =>
            item.Kind == CentralObjectRecoveryKinds.ExpiredDelete
            && item.OperationToken != null
            && item.State == CentralObjectRecoveryStates.Failed, cancellationToken).ConfigureAwait(false);
        if (failed)
        {
            return new HealthCheckResult(
                HealthStatus.Unhealthy,
                "A retention deletion requires operator intervention.",
                data: CreateData("failed", 0, 0, 0));
        }

        var pending = await (from disposition in dbContext.CentralObjectRecoveryDispositions.AsNoTracking()
                             join artifact in dbContext.CentralArtifacts.AsNoTracking()
                                 on disposition.CentralArtifactId equals (Guid?)artifact.Id
                             where disposition.Kind == CentralObjectRecoveryKinds.ExpiredDelete
                                 && disposition.OperationToken != null
                                 && disposition.State == CentralObjectRecoveryStates.PendingDelete
                                 && artifact.RetentionDeletionToken == disposition.OperationToken
                                 && artifact.RetentionDeletionRequestedAtUtc != null
                             select new
                             {
                                 disposition.ByteLength,
                                 RequestedAtUtc = artifact.RetentionDeletionRequestedAtUtc!.Value
                             })
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.LongCount(),
                Bytes = group.Sum(item => item.ByteLength),
                Oldest = group.Min(item => item.RequestedAtUtc)
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (pending is null)
        {
            return new HealthCheckResult(
                HealthStatus.Healthy,
                "The retention deletion queue is drained.",
                data: CreateData("drained", 0, 0, 0));
        }

        var oldestAge = Math.Max(0, (long)(timeProvider.GetUtcNow() - pending.Oldest).TotalSeconds);
        var stale = oldestAge >= options.Value.StalePendingThreshold.TotalSeconds;
        return new HealthCheckResult(
            stale ? HealthStatus.Degraded : HealthStatus.Healthy,
            stale
                ? "Retention deletion work has exceeded the recovery window."
                : "Retention deletion work is pending within the recovery window.",
            data: CreateData(stale ? "stale-pending" : "pending", pending.Count, pending.Bytes, oldestAge));
    }

    private static Dictionary<string, object> CreateData(
        string condition,
        long count,
        long bytes,
        long oldestAgeSeconds)
        => new()
        {
            ["Condition"] = condition,
            ["PendingCount"] = count,
            ["PendingBytes"] = bytes,
            ["PendingOldestAgeSeconds"] = oldestAgeSeconds
        };
}
