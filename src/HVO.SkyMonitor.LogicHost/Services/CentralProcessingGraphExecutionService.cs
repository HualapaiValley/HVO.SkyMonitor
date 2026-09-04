using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralProcessingGraphExecutionSummary(
    Guid Id,
    string ExecutionClass,
    string Status,
    Guid RevisionId,
    Guid ObservatoryId,
    Guid LogicalCameraId,
    Guid InstallationPublicId,
    Guid AnchorSourceArtifactId,
    string Trigger,
    string ActorId,
    string ReasonCode,
    int SourceCount,
    int NodeCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc);

internal sealed record CentralProcessingGraphExecutionSourceView(
    int Ordinal,
    string SourceId,
    Guid ArtifactId,
    string ArtifactChecksumSha256,
    long ArtifactByteLength,
    DateTimeOffset SelectedAtUtc);

internal sealed record CentralProcessingGraphNodeView(
    Guid JobId,
    int Ordinal,
    string NodeId,
    string Status,
    string FailurePolicy,
    string? ReasonCode,
    int AttemptCount,
    int InputCount,
    int RequiredInputCount,
    int ResolvedInputCount,
    IReadOnlyList<CentralProcessingGraphOutputView> Outputs);

internal sealed record CentralProcessingGraphOutputView(
    int Ordinal,
    string Role,
    string Variant,
    string ProductKind,
    Guid? ArtifactId,
    string? OutputIdentitySha256,
    DateTimeOffset? BoundAtUtc);

internal sealed record CentralProcessingGraphExecutionDetail(
    CentralProcessingGraphExecutionSummary Execution,
    IReadOnlyList<CentralProcessingGraphExecutionSourceView> Sources,
    IReadOnlyList<CentralProcessingGraphNodeView> Nodes);

internal enum CentralProcessingGraphCancellationOutcome
{
    Applied,
    Unchanged,
    NotFoundOrDenied,
    Forbidden
}

internal interface ICentralProcessingGraphExecutionService
{
    Task<CentralProcessingGraphScheduleResult> ScheduleReplayAsync(
        Guid revisionId,
        IReadOnlyList<Guid> sourceArtifactIds,
        string actorId,
        Guid? observatoryScope,
        string idempotencyKey,
        string reasonCode,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CentralProcessingGraphExecutionSummary>> ListAsync(
        string actorId,
        Guid? observatoryScope,
        int take,
        CancellationToken cancellationToken);

    Task<CentralProcessingGraphExecutionDetail?> GetAsync(
        Guid executionId,
        string actorId,
        Guid? observatoryScope,
        CancellationToken cancellationToken);

    Task<CentralProcessingGraphCancellationOutcome> CancelAsync(
        Guid executionId,
        string actorId,
        Guid? observatoryScope,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

internal sealed class CentralProcessingGraphExecutionService(
    ApplicationDbContext dbContext,
    ICentralProcessingGraphScheduler scheduler,
    CentralProcessingGraphConvergenceSignal convergenceSignal) : ICentralProcessingGraphExecutionService
{
    public async Task<CentralProcessingGraphScheduleResult> ScheduleReplayAsync(
        Guid revisionId,
        IReadOnlyList<Guid> sourceArtifactIds,
        string actorId,
        Guid? observatoryScope,
        string idempotencyKey,
        string reasonCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceArtifactIds);
        var centralArtifactIds = await ResolveAuthorizedSourcesAsync(
            sourceArtifactIds, actorId, observatoryScope, cancellationToken).ConfigureAwait(false);
        if (centralArtifactIds is null)
        {
            return new(CentralProcessingGraphScheduleOutcome.Invalid, ReasonCode: "sources-not-found-or-denied");
        }
        return await scheduler.ScheduleReplayAsync(new(
            revisionId, centralArtifactIds, actorId, idempotencyKey, reasonCode), now, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CentralProcessingGraphExecutionSummary>> ListAsync(
        string actorId,
        Guid? observatoryScope,
        int take,
        CancellationToken cancellationToken)
        => await AuthorizedQuery(actorId, observatoryScope)
            .OrderByDescending(execution => execution.CreatedAtUtc)
            .ThenByDescending(execution => execution.Id)
            .Select(execution => new CentralProcessingGraphExecutionSummary(
                execution.Id,
                execution.ExecutionClass.ToString(),
                execution.Status.ToString(),
                execution.RevisionId,
                execution.ObservatoryId,
                execution.LogicalCameraId,
                execution.InstallationPublicId,
                execution.AnchorSourceArtifactId,
                execution.Trigger.ToString(),
                execution.ActorId,
                execution.ReasonCode,
                execution.Sources.Count,
                execution.Jobs.Count,
                execution.CreatedAtUtc,
                execution.UpdatedAtUtc,
                execution.CompletedAtUtc))
            .Take(take)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public async Task<CentralProcessingGraphExecutionDetail?> GetAsync(
        Guid executionId,
        string actorId,
        Guid? observatoryScope,
        CancellationToken cancellationToken)
    {
        var execution = await AuthorizedQuery(actorId, observatoryScope)
            .Include(item => item.Sources)
            .Include(item => item.Jobs).ThenInclude(job => job.InputRequirements)
            .Include(item => item.Jobs).ThenInclude(job => job.Inputs)
            .Include(item => item.Jobs).ThenInclude(job => job.CanonicalInputs)
            .Include(item => item.Jobs).ThenInclude(job => job.Outputs).ThenInclude(output => output.ResultArtifact)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.Id == executionId, cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            return null;
        }
        var summary = new CentralProcessingGraphExecutionSummary(
            execution.Id,
            execution.ExecutionClass.ToString(),
            execution.Status.ToString(),
            execution.RevisionId,
            execution.ObservatoryId,
            execution.LogicalCameraId,
            execution.InstallationPublicId,
            execution.AnchorSourceArtifactId,
            execution.Trigger.ToString(),
            execution.ActorId,
            execution.ReasonCode,
            execution.Sources.Count,
            execution.Jobs.Count,
            execution.CreatedAtUtc,
            execution.UpdatedAtUtc,
            execution.CompletedAtUtc);
        return new(
            summary,
            execution.Sources.OrderBy(source => source.Ordinal).Select(source =>
                new CentralProcessingGraphExecutionSourceView(
                    source.Ordinal,
                    source.SourceId,
                    source.ArtifactId,
                    source.ArtifactChecksumSha256,
                    source.ArtifactByteLength,
                    source.SelectedAtUtc)).ToArray(),
            execution.Jobs.OrderBy(job => job.GraphNodeOrdinal).Select(job =>
                new CentralProcessingGraphNodeView(
                    job.Id,
                    job.GraphNodeOrdinal!.Value,
                    job.GraphNodeId!,
                    job.Status.ToString(),
                    job.GraphFailurePolicy!.Value.ToString(),
                    job.StateReasonCode ?? job.LastError,
                    job.AttemptCount,
                    job.InputRequirements.Count,
                    job.InputRequirements.Count(requirement => requirement.IsRequired),
                    job.InputRequirements.Count(requirement =>
                        requirement.ResolutionState == CentralDerivativeInputResolutionState.Resolved),
                    job.Outputs.OrderBy(output => output.Ordinal).Select(output =>
                        new CentralProcessingGraphOutputView(
                            output.Ordinal,
                            output.Role.ToString(),
                            output.Variant,
                            output.ProductKind.ToString(),
                            output.ResultArtifact?.ArtifactId,
                            output.ResultOutputIdentitySha256,
                            output.BoundAtUtc)).ToArray())).ToArray());
    }

    public async Task<CentralProcessingGraphCancellationOutcome> CancelAsync(
        Guid executionId,
        string actorId,
        Guid? observatoryScope,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        if (dbContext.Database.IsSqlServer())
        {
            _ = await dbContext.Database.SqlQuery<int>($"""
                SELECT CAST(1 AS int) AS [Value]
                FROM [CentralProcessingGraphExecutions] WITH (UPDLOCK, HOLDLOCK)
                WHERE [Id] = {executionId}
                """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
        var execution = await AuthorizedQuery(actorId, observatoryScope)
            .SingleOrDefaultAsync(item => item.Id == executionId, cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return CentralProcessingGraphCancellationOutcome.NotFoundOrDenied;
        }
        // Reading an execution needs only membership; stopping one is a management action. A Viewer who requested
        // their own replay may still cancel it, but never a live ingest execution or another member's replay.
        if (!await HasCancellationAuthorityAsync(execution, actorId, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return CentralProcessingGraphCancellationOutcome.Forbidden;
        }
        if (IsTerminal(execution.Status) || execution.Status == CentralProcessingGraphExecutionStatus.CancelRequested)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return CentralProcessingGraphCancellationOutcome.Unchanged;
        }
        execution.Status = CentralProcessingGraphExecutionStatus.CancelRequested;
        execution.CancellationRequestedAtUtc = now;
        execution.UpdatedAtUtc = now;
        // Record the requesting actor on every nonterminal node now; CentralDerivativeJob remains the sole
        // lease/execution authority, so convergence finalizes status later and only fills missing actors.
        var nonterminalJobs = await dbContext.CentralDerivativeJobs
            .Where(job => job.GraphExecutionId == execution.Id &&
                job.Status != CentralDerivativeJobStatus.Completed &&
                job.Status != CentralDerivativeJobStatus.TerminalFailure &&
                job.Status != CentralDerivativeJobStatus.Canceled &&
                job.Status != CentralDerivativeJobStatus.Skipped &&
                job.Status != CentralDerivativeJobStatus.Quarantined &&
                job.Status != CentralDerivativeJobStatus.Superseded &&
                job.CancellationRequestedAtUtc == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var job in nonterminalJobs)
        {
            job.CancellationRequestedAtUtc = now;
            job.CancellationRequestedBy = actorId;
            job.UpdatedAtUtc = now;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        convergenceSignal.Signal(execution.Id);
        return CentralProcessingGraphCancellationOutcome.Applied;
    }

    private async Task<Guid[]?> ResolveAuthorizedSourcesAsync(
        IReadOnlyList<Guid> sourceArtifactIds,
        string actorId,
        Guid? observatoryScope,
        CancellationToken cancellationToken)
    {
        if (sourceArtifactIds.Count == 0 || sourceArtifactIds.Distinct().Count() != sourceArtifactIds.Count ||
            string.IsNullOrWhiteSpace(actorId))
        {
            return null;
        }
        var authorizedObservatories = ObservatoryMembershipAccess.ForUser(dbContext, actorId)
            .Select(membership => membership.ObservatoryId);
        if (observatoryScope.HasValue)
        {
            authorizedObservatories = authorizedObservatories.Where(id => id == observatoryScope.Value);
        }
        var sources = await dbContext.CentralArtifacts.AsNoTracking()
            .Where(artifact => sourceArtifactIds.Contains(artifact.ArtifactId) &&
                authorizedObservatories.Contains(artifact.Frame!.ObservatoryId))
            .Select(artifact => new { CentralArtifactId = artifact.Id, artifact.ArtifactId, artifact.Frame!.ObservatoryId })
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (sources.Length != sourceArtifactIds.Count || sources.Select(source => source.ObservatoryId).Distinct().Count() != 1)
        {
            return null;
        }
        return sourceArtifactIds.Select(id => sources.Single(source => source.ArtifactId == id).CentralArtifactId).ToArray();
    }

    private async Task<bool> HasCancellationAuthorityAsync(
        CentralProcessingGraphExecution execution,
        string actorId,
        CancellationToken cancellationToken)
        => execution.ExecutionClass == CentralProcessingGraphExecutionClass.Replay &&
                string.Equals(execution.ActorId, actorId, StringComparison.Ordinal)
            || await ObservatoryMembershipAccess.ForManager(dbContext, actorId)
                .AnyAsync(membership => membership.ObservatoryId == execution.ObservatoryId, cancellationToken)
                .ConfigureAwait(false);

    private IQueryable<CentralProcessingGraphExecution> AuthorizedQuery(string actorId, Guid? observatoryScope)
    {
        var authorizedObservatories = ObservatoryMembershipAccess.ForUser(dbContext, actorId)
            .Select(membership => membership.ObservatoryId);
        if (observatoryScope.HasValue)
        {
            authorizedObservatories = authorizedObservatories.Where(id => id == observatoryScope.Value);
        }
        return dbContext.CentralProcessingGraphExecutions.Where(execution =>
            authorizedObservatories.Contains(execution.ObservatoryId));
    }

    private static bool IsTerminal(CentralProcessingGraphExecutionStatus status)
        => status is CentralProcessingGraphExecutionStatus.Completed or
            CentralProcessingGraphExecutionStatus.CompletedWithOptionalFailures or
            CentralProcessingGraphExecutionStatus.Failed or
            CentralProcessingGraphExecutionStatus.Canceled or
            CentralProcessingGraphExecutionStatus.Superseded;
}
