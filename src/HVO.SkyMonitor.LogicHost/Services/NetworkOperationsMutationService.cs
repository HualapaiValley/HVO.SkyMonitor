using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal enum NetworkOperationsMutationOutcome
{
    Applied,
    NotFoundOrDenied,
    InvalidState
}

internal interface INetworkOperationsMutationService
{
    Task<NetworkOperationsMutationOutcome> CancelJobAsync(string actorUserId, Guid jobId, CancellationToken cancellationToken = default);
    Task<NetworkOperationsMutationOutcome> RequeueJobAsync(string actorUserId, Guid jobId, CancellationToken cancellationToken = default);
    Task<NetworkOperationsMutationOutcome> ReprocessJobAsync(string actorUserId, Guid jobId, CancellationToken cancellationToken = default);
}

internal sealed class NetworkOperationsMutationService(
    ApplicationDbContext dbContext,
    ICentralDerivativeJobOperationsService jobOperations,
    ILogger<NetworkOperationsMutationService> logger,
    OperatorUiTelemetry telemetry) : INetworkOperationsMutationService
{
    public Task<NetworkOperationsMutationOutcome> CancelJobAsync(string actorUserId, Guid jobId, CancellationToken cancellationToken = default)
        => MutateAsync(
            actorUserId,
            jobId,
            (actor, precondition, token) => jobOperations.CancelAsync(jobId, actor, token, precondition),
            cancellationToken);

    public Task<NetworkOperationsMutationOutcome> RequeueJobAsync(string actorUserId, Guid jobId, CancellationToken cancellationToken = default)
        => MutateAsync(
            actorUserId,
            jobId,
            (actor, precondition, token) => jobOperations.RequeueAsync(jobId, actor, token, precondition),
            cancellationToken);

    public async Task<NetworkOperationsMutationOutcome> ReprocessJobAsync(string actorUserId, Guid jobId, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartMutation("central-job-reprocess");
        var job = await AuthorizedJob(actorUserId, jobId).Select(item => new
        {
            item.Job.RecipeName,
            item.Job.RecipeOptionsJson,
            item.Job.SourceArtifact!.Frame!.ObservatoryId,
            item.EffectiveRole
        }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            telemetry.RecordMutation("central-job-reprocess", "denied", "manager", Stopwatch.GetElapsedTime(started));
            return NetworkOperationsMutationOutcome.NotFoundOrDenied;
        }
        try
        {
            using var options = JsonDocument.Parse(job.RecipeOptionsJson);
            _ = await jobOperations.ReprocessAsync(
                jobId,
                new CentralDerivativeReprocessRequest(job.RecipeName, options.RootElement.Clone(), "manual", false),
                $"user:{actorUserId}",
                cancellationToken,
                token => HasManagerAuthorityWithLockAsync(actorUserId, job.ObservatoryId, token)).ConfigureAwait(false);
            telemetry.RecordMutation(
                "central-job-reprocess",
                "applied",
                job.EffectiveRole.ToString().ToLowerInvariant(),
                Stopwatch.GetElapsedTime(started));
            OperatorUiAuditLog.CentralOverride(logger, "central-job-reprocess", "applied");
            return NetworkOperationsMutationOutcome.Applied;
        }
        catch (CentralDerivativeJobStateException)
        {
            telemetry.RecordMutation(
                "central-job-reprocess",
                "invalid-state",
                job.EffectiveRole.ToString().ToLowerInvariant(),
                Stopwatch.GetElapsedTime(started));
            OperatorUiAuditLog.CentralOverride(logger, "central-job-reprocess", "invalid-state");
            return NetworkOperationsMutationOutcome.InvalidState;
        }
        catch (UnauthorizedAccessException)
        {
            telemetry.RecordMutation("central-job-reprocess", "denied", "manager", Stopwatch.GetElapsedTime(started));
            return NetworkOperationsMutationOutcome.NotFoundOrDenied;
        }
    }

    private async Task<NetworkOperationsMutationOutcome> MutateAsync(
        string actorUserId,
        Guid jobId,
        Func<string, Func<CancellationToken, Task<bool>>, CancellationToken, Task> mutation,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = OperatorUiTelemetry.StartMutation("central-job");
        ArgumentException.ThrowIfNullOrWhiteSpace(actorUserId);
        var authority = await AuthorizedJob(actorUserId, jobId)
            .Select(item => new
            {
                item.Job.SourceArtifact!.Frame!.ObservatoryId,
                item.EffectiveRole
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (authority is null)
        {
            telemetry.RecordMutation("central-job", "denied", "manager", Stopwatch.GetElapsedTime(started));
            return NetworkOperationsMutationOutcome.NotFoundOrDenied;
        }
        try
        {
            await mutation(
                $"user:{actorUserId}",
                token => HasManagerAuthorityWithLockAsync(actorUserId, authority.ObservatoryId, token),
                cancellationToken).ConfigureAwait(false);
            telemetry.RecordMutation("central-job", "applied", authority.EffectiveRole.ToString().ToLowerInvariant(), Stopwatch.GetElapsedTime(started));
            OperatorUiAuditLog.CentralOverride(logger, "central-job", "applied");
            return NetworkOperationsMutationOutcome.Applied;
        }
        catch (CentralDerivativeJobStateException)
        {
            telemetry.RecordMutation("central-job", "invalid-state", authority.EffectiveRole.ToString().ToLowerInvariant(), Stopwatch.GetElapsedTime(started));
            OperatorUiAuditLog.CentralOverride(logger, "central-job", "invalid-state");
            return NetworkOperationsMutationOutcome.InvalidState;
        }
        catch (UnauthorizedAccessException)
        {
            telemetry.RecordMutation("central-job", "denied", "manager", Stopwatch.GetElapsedTime(started));
            return NetworkOperationsMutationOutcome.NotFoundOrDenied;
        }
    }

    private Task<bool> HasManagerAuthorityWithLockAsync(
        string actorUserId,
        Guid observatoryId,
        CancellationToken cancellationToken)
        => dbContext.ObservatoryMemberships.FromSqlInterpolated($"""
                SELECT membership.*
                FROM [ObservatoryMemberships] AS membership WITH (UPDLOCK, HOLDLOCK)
                WHERE membership.[UserId] = {actorUserId}
                  AND membership.[ObservatoryId] = {observatoryId}
                  AND membership.[Role] IN (N'Manager', N'Owner')
                """)
            .AsNoTracking()
            .AnyAsync(cancellationToken);

    private IQueryable<AuthorizedJobRow> AuthorizedJob(string actorUserId, Guid jobId)
        => dbContext.CentralDerivativeJobs.Where(job => job.Id == jobId)
            .Join(ObservatoryMembershipAccess.ForManager(dbContext, actorUserId),
                job => job.SourceArtifact!.Frame!.ObservatoryId,
                membership => membership.ObservatoryId,
                (job, membership) => new AuthorizedJobRow(job, membership.Role));

    private sealed record AuthorizedJobRow(CentralDerivativeJob Job, ObservatoryMembershipRole EffectiveRole);
}
