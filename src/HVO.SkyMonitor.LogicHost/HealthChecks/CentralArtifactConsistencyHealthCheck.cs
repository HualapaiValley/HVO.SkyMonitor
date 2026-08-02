using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.LogicHost.HealthChecks;

internal sealed class CentralArtifactConsistencyHealthCheck(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    CentralRecoveryStartupState startupState) : IHealthCheck
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(5);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var cutoffUtc = timeProvider.GetUtcNow() - StaleAfter;
        var checkpoint = await dbContext.CentralRecoveryCheckpoints.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == CentralRecoveryCheckpoint.SingletonId, cancellationToken)
            .ConfigureAwait(false);
        if (checkpoint is null)
        {
            return CreateInventoryResult(
                HealthStatus.Unhealthy,
                "inventory-unavailable",
                "Central artifact recovery inventory has no durable checkpoint.");
        }
        var progressAtUtc = checkpoint.LastProgressAtUtc ?? checkpoint.InventoryStartedAtUtc;
        var leaseCurrent = checkpoint.LeaseExpiresAtUtc > timeProvider.GetUtcNow();
        if (checkpoint.Phase != CentralRecoveryPhases.Idle
            && !leaseCurrent
            && (!progressAtUtc.HasValue || progressAtUtc <= cutoffUtc))
        {
            return CreateInventoryResult(
                HealthStatus.Unhealthy,
                "inventory-stalled",
                "Central artifact recovery inventory has stopped making progress.");
        }
        if (checkpoint.Phase == CentralRecoveryPhases.Idle
            && !checkpoint.LastCompletedAtUtc.HasValue
            && checkpoint.NextInventoryAtUtc <= timeProvider.GetUtcNow())
        {
            var startupExpired = timeProvider.GetUtcNow() - startupState.StartedAtUtc >= StartupGrace;
            return CreateInventoryResult(
                startupExpired ? HealthStatus.Unhealthy : HealthStatus.Degraded,
                startupExpired ? "inventory-stalled" : "inventory-starting",
                startupExpired
                    ? "Central artifact recovery inventory did not complete during startup grace."
                    : "Central artifact recovery inventory is awaiting its first startup completion.");
        }
        var durableArtifactFinding = await dbContext.CentralArtifacts.AsNoTracking()
            .AnyAsync(artifact => artifact.ObjectState == CentralArtifactObjectState.Quarantined
                || artifact.ReconstructionState == CentralReconstructionState.Quarantined
                || artifact.StateReasonCode == "object.missing", cancellationToken)
            .ConfigureAwait(false);
        var durableDispositionFinding = await dbContext.CentralObjectRecoveryDispositions.AsNoTracking()
            .AnyAsync(item => (item.Kind == CentralObjectRecoveryKinds.OrphanQuarantine
                    && item.State != CentralObjectRecoveryStates.Cancelled)
                || item.State == CentralObjectRecoveryStates.Failed, cancellationToken)
            .ConfigureAwait(false);
        if (durableArtifactFinding || durableDispositionFinding)
        {
            return CreateInventoryResult(
                HealthStatus.Degraded,
                "inventory-findings",
                "Central artifact recovery inventory has durable findings requiring operator review.");
        }
        if (checkpoint.Phase != CentralRecoveryPhases.Idle)
        {
            return CreateInventoryResult(
                HealthStatus.Degraded,
                "inventory-incomplete",
                "Central artifact recovery inventory has not completed its current generation.");
        }

        var hasStaleConsistencyBacklog = await dbContext.CentralArtifacts.AsNoTracking()
            .AnyAsync(artifact => artifact.ObjectVerificationToken != null
                    && artifact.ObjectVerificationRequestedAtUtc <= cutoffUtc
                || artifact.ReceivedAtUtc <= cutoffUtc
                && artifact.ObjectVerificationToken == null
                && (artifact.ObjectState == CentralArtifactObjectState.Pending
                    || artifact.ObjectState == CentralArtifactObjectState.Quarantined
                    || artifact.ReconstructionState == CentralReconstructionState.PendingReference
                    || artifact.ReconstructionState == CentralReconstructionState.Quarantined), cancellationToken)
            .ConfigureAwait(false);
        return hasStaleConsistencyBacklog
            ? HealthCheckResult.Degraded(
                "Central artifact consistency state has exceeded the reconciliation window.",
                data: new Dictionary<string, object>
                {
                    ["Condition"] = "stale-consistency-backlog",
                    ["ReconciliationWindowSeconds"] = (long)StaleAfter.TotalSeconds
                })
            : HealthCheckResult.Healthy("Central artifact consistency and recovery inventory are current.");
    }

    private static HealthCheckResult CreateInventoryResult(
        HealthStatus status,
        string condition,
        string description)
        => new(status, description, data: new Dictionary<string, object>
        {
            ["Condition"] = condition,
            ["ReconciliationWindowSeconds"] = (long)StaleAfter.TotalSeconds
        });
}

internal sealed class CentralRecoveryStartupState
{
    public CentralRecoveryStartupState(TimeProvider timeProvider)
        : this(timeProvider.GetUtcNow())
    {
    }

    internal CentralRecoveryStartupState(DateTimeOffset startedAtUtc)
    {
        StartedAtUtc = startedAtUtc;
    }

    public DateTimeOffset StartedAtUtc { get; }
}
