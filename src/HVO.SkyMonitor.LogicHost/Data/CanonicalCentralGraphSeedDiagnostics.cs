using HVO.SkyMonitor.LogicHost.Services;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>What startup seeding found that the current binary will not execute as frozen.</summary>
internal sealed record CanonicalCentralGraphSeedReport(
    int FrozenIdentityMismatchJobCount,
    int SupersededSeedExecutionCount,
    IReadOnlyList<CanonicalCentralGraphStaleAssignment> StaleAssignments);

/// <summary>An active central assignment whose revision this binary can no longer prepare.</summary>
internal sealed record CanonicalCentralGraphStaleAssignment(
    Guid AssignmentId,
    Guid RevisionId,
    CentralProcessingGraphAssignmentScope Scope,
    Guid? ObservatoryId,
    Guid? LogicalCameraId,
    string ReasonCode,
    string? UnsupportedNode);

/// <summary>
/// Counts and logs, without rewriting anything, the work a canonical seed supersession leaves behind.
/// </summary>
/// <remarks>
/// <para>
/// A graph execution freezes its revision, plan and every node's requested recipe identity when it is expanded, so a
/// supersession never re-plans it. Each still non-terminal job whose frozen requested identity differs from the one
/// this binary derives is failed terminally with <c>processing.recipe-identity-mismatch</c> on its first lease
/// (<see cref="CentralDerivativeJobExecutor.MatchesCurrentRequestedIdentity"/>, used here as well), without retry, and
/// graph convergence then fails its required dependents and the execution. The count is a bounded, grouped query.
/// </para>
/// <para>
/// Operator assignments cannot be backdated, so they still take precedence over every seed entry. An active one whose
/// revision this binary cannot expand, typically because it embeds a built-in recipe definition from an earlier
/// <c>ImplementationVersion</c>, fails <see cref="CentralProcessingGraphPlanVerification"/> before any job exists. Live
/// scheduling then throws during ingest, the upload is answered with HTTP 500, and the edge retains and retries the
/// frame until an operator ends or replaces the assignment. Each one is logged with the effect and the identifiers an
/// operator needs; none is rewritten. The same finder backs the processing graph catalog health check.
/// </para>
/// </remarks>
internal static partial class CanonicalCentralGraphSeedDiagnostics
{
    private static readonly CentralDerivativeJobStatus[] NonTerminalStatuses =
    [
        CentralDerivativeJobStatus.Waiting,
        CentralDerivativeJobStatus.Pending,
        CentralDerivativeJobStatus.Leased,
        CentralDerivativeJobStatus.RetryableFailure
    ];

    internal static async Task<CanonicalCentralGraphSeedReport> ReportAsync(
        ApplicationDbContext dbContext,
        IReadOnlyList<CanonicalCentralGraphSeedRevision> chain,
        ICentralProcessingGraphNodeRegistry nodeRegistry,
        DateTimeOffset now,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(nodeRegistry);
        ArgumentNullException.ThrowIfNull(logger);
        var mismatched = await CountFrozenIdentityMismatchesAsync(dbContext);
        var historicalIds = chain.Take(chain.Count - 1).Select(static entry => entry.RevisionId).ToArray();
        var supersededExecutions = historicalIds.Length == 0
            ? 0
            : await dbContext.CentralProcessingGraphExecutions.AsNoTracking()
                .CountAsync(item => historicalIds.Contains(item.RevisionId) &&
                    (item.Status == CentralProcessingGraphExecutionStatus.Pending ||
                     item.Status == CentralProcessingGraphExecutionStatus.Running));
        var stale = await FindStaleAssignmentsAsync(dbContext, nodeRegistry, now);
        if (mismatched > 0 || supersededExecutions > 0)
        {
            Log.FrozenWork(logger, chain[^1].Revision, supersededExecutions, mismatched);
        }
        foreach (var assignment in stale)
        {
            Log.StaleAssignment(logger, assignment.AssignmentId, assignment.RevisionId,
                assignment.UnsupportedNode ?? "(none)", assignment.ReasonCode, Describe(assignment));
        }
        return new(mismatched, supersededExecutions, stale);
    }

    private static async Task<int> CountFrozenIdentityMismatchesAsync(ApplicationDbContext dbContext)
    {
        // A requested identity hashes recipe, options and selector, so one representative per distinct identity
        // decides the whole group; the query is bounded by distinct identities, not by backlog size.
        var groups = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(item => NonTerminalStatuses.Contains(item.Status))
            .GroupBy(item => new { item.RecipeName, item.RequestedRecipeIdentitySha256 })
            .Select(group => new { group.Key.RecipeName, group.Key.RequestedRecipeIdentitySha256, Count = group.Count() })
            .ToListAsync();
        var total = 0;
        foreach (var group in groups)
        {
            if (CentralDerivativeJobExecutor.IsInProcessOnlyRecipe(group.RecipeName))
            {
                continue;
            }
            var sample = await dbContext.CentralDerivativeJobs.AsNoTracking()
                .Where(item => NonTerminalStatuses.Contains(item.Status) && item.RecipeName == group.RecipeName &&
                    item.RequestedRecipeIdentitySha256 == group.RequestedRecipeIdentitySha256)
                .Select(item => new { item.RecipeOptionsJson, item.InputSelectorJson })
                .FirstAsync();
            bool matches;
            try
            {
                matches = CentralDerivativeJobExecutor.MatchesCurrentRequestedIdentity(
                    group.RecipeName,
                    sample.RecipeOptionsJson,
                    sample.InputSelectorJson,
                    group.RequestedRecipeIdentitySha256);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                System.Text.Json.JsonException or CentralDerivativeJobStateException)
            {
                matches = false;
            }
            if (!matches)
            {
                total += group.Count;
            }
        }
        return total;
    }

    /// <summary>
    /// Every active central assignment that wins resolution within its own scope target and whose revision this
    /// binary cannot expand, decided by the same <see cref="CentralProcessingGraphPlanVerification"/> that live and
    /// replay scheduling use. An assignment outranked within its scope target (every historical seed assignment, for
    /// one) never resolves, so it is not reported however stale its revision is.
    /// </summary>
    internal static async Task<IReadOnlyList<CanonicalCentralGraphStaleAssignment>> FindStaleAssignmentsAsync(
        ApplicationDbContext dbContext,
        ICentralProcessingGraphNodeRegistry nodeRegistry,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(nodeRegistry);
        // Eligibility mirrors ProcessingGraphCatalogService.ResolveAsync; the winner of each scope target is the
        // assignment that call would return for a frame that scope target alone covers.
        var eligible = await dbContext.CentralProcessingGraphAssignments.AsNoTracking()
            .Where(item => item.TargetHost == CentralProcessingGraphTargetHost.Central &&
                item.EffectiveFromUtc <= now &&
                (item.EffectiveUntilUtc == null || now < item.EffectiveUntilUtc) &&
                item.Revision!.PublishedAtUtc <= now &&
                (item.Revision.RetiredAtUtc == null || now < item.Revision.RetiredAtUtc))
            .Select(item => new
            {
                item.Id,
                item.RevisionId,
                item.Scope,
                item.ObservatoryId,
                item.LogicalCameraId,
                item.EffectiveFromUtc,
                item.CreatedAtUtc
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var winners = eligible
            .GroupBy(item => (item.Scope, item.ObservatoryId, item.LogicalCameraId))
            .Select(group => group
                .OrderByDescending(item => item.EffectiveFromUtc)
                .ThenByDescending(item => item.CreatedAtUtc)
                .ThenBy(item => item.Id)
                .First())
            .OrderBy(item => item.Id)
            .ToArray();
        var revisionIds = winners.Select(item => item.RevisionId).Distinct().ToArray();
        var revisions = await dbContext.CentralProcessingGraphRevisions.AsNoTracking()
            .Where(item => revisionIds.Contains(item.Id))
            .Select(item => new
            {
                item.Id,
                item.DefinitionJson,
                item.DefinitionIdentitySha256,
                item.CentralPlanIdentitySha256
            })
            .ToDictionaryAsync(item => item.Id, cancellationToken).ConfigureAwait(false);
        var verified = revisions.Values.ToDictionary(
            static item => item.Id,
            item => CentralProcessingGraphPlanVerification.Verify(
                item.DefinitionJson, item.DefinitionIdentitySha256, item.CentralPlanIdentitySha256, nodeRegistry));
        var stale = new List<CanonicalCentralGraphStaleAssignment>();
        foreach (var winner in winners)
        {
            if (verified[winner.RevisionId] is { Plan: null } failure)
            {
                stale.Add(new(winner.Id, winner.RevisionId, winner.Scope, winner.ObservatoryId, winner.LogicalCameraId,
                    failure.FailureReasonCode!, failure.UnsupportedNode));
            }
        }
        return stale;
    }

    internal static string Describe(CanonicalCentralGraphStaleAssignment assignment)
        => assignment.Scope switch
        {
            CentralProcessingGraphAssignmentScope.LogicalCamera => $"logical camera {assignment.LogicalCameraId}",
            CentralProcessingGraphAssignmentScope.Observatory =>
                $"every camera of observatory {assignment.ObservatoryId} without a camera assignment",
            _ => "every camera without an observatory or camera assignment"
        };

    private static partial class Log
    {
        [LoggerMessage(1030, LogLevel.Warning,
            "Canonical central graph seed {Revision} is current. {ExecutionCount} non-terminal graph executions " +
            "remain frozen on a superseded seed revision, and {JobCount} non-terminal jobs carry a requested recipe " +
            "identity this binary no longer derives; each such job fails terminally with " +
            "processing.recipe-identity-mismatch on its first lease.")]
        internal static partial void FrozenWork(ILogger logger, string revision, int executionCount, int jobCount);

        [LoggerMessage(1031, LogLevel.Error,
            "Central processing graph assignment {AssignmentId} resolves revision {RevisionId}, which this binary " +
            "cannot expand (node {NodeAlias}: {ReasonCode}). Frames for {AffectedCameras} fail ingest with HTTP 500 " +
            "and accumulate on the edge until an operator ends this assignment or reassigns a revision published on " +
            "the current recipe versions; startup never rewrites assignments.")]
        internal static partial void StaleAssignment(
            ILogger logger, Guid assignmentId, Guid revisionId, string nodeAlias, string reasonCode, string affectedCameras);
    }
}
