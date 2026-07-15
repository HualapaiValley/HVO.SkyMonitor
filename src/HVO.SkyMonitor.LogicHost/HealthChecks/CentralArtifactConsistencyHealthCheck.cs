using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

internal sealed class CentralArtifactConsistencyHealthCheck(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : IHealthCheck
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var cutoffUtc = timeProvider.GetUtcNow() - StaleAfter;
        var stale = await dbContext.CentralArtifacts
            .AsNoTracking()
            .Where(artifact => artifact.ReceivedAtUtc <= cutoffUtc
                && (artifact.ObjectState == CentralArtifactObjectState.Pending
                    || artifact.ObjectState == CentralArtifactObjectState.Quarantined
                    || artifact.ReconstructionState == CentralReconstructionState.PendingReference
                    || artifact.ReconstructionState == CentralReconstructionState.Quarantined))
            .OrderBy(artifact => artifact.ReceivedAtUtc)
            .Select(artifact => (Guid?)artifact.Id)
            .Take(1)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (stale is null)
        {
            return HealthCheckResult.Healthy("No stale central artifact consistency state was found.");
        }

        return HealthCheckResult.Degraded(
            "Central artifact consistency state has exceeded the reconciliation window.",
            data: new Dictionary<string, object>
            {
                ["Condition"] = "stale-consistency-backlog",
                ["ReconciliationWindowSeconds"] = (long)StaleAfter.TotalSeconds
            });
    }
}
