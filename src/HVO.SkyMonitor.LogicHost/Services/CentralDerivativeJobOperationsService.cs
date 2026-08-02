using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Diagnostics;
using System.Text.Json;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralDerivativeReprocessRequest(
    string RecipeName,
    JsonElement Options,
    string OutputVariant,
    bool Supersede);

internal interface ICentralDerivativeJobOperationsService
{
    Task CancelAsync(
        Guid jobId,
        string actor,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? transactionPrecondition = null);

    Task RequeueAsync(
        Guid jobId,
        string actor,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? transactionPrecondition = null);

    Task<Guid> ReprocessAsync(
        Guid jobId,
        CentralDerivativeReprocessRequest request,
        string actor,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? transactionPrecondition = null);
}

internal sealed partial class CentralDerivativeJobOperationsService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    CentralDerivativeWorkerTelemetry telemetry,
    ILogger<CentralDerivativeJobOperationsService> logger) : ICentralDerivativeJobOperationsService
{
    public async Task CancelAsync(
        Guid jobId,
        string actor,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? transactionPrecondition = null)
    {
        ValidateActor(actor);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _ = await CentralDerivativeJobLock.AcquireAsync(dbContext, jobId, cancellationToken).ConfigureAwait(false);
        if (transactionPrecondition is not null
            && !await transactionPrecondition(cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new UnauthorizedAccessException("The transaction precondition no longer permits this job mutation.");
        }
        var job = await dbContext.CentralDerivativeJobs.Include(candidate => candidate.Inputs).SingleOrDefaultAsync(
            candidate => candidate.Id == jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative job does not exist.");
        if (job.Status == CentralDerivativeJobStatus.Canceled)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (job.Status is CentralDerivativeJobStatus.Completed
            or CentralDerivativeJobStatus.Superseded
            or CentralDerivativeJobStatus.Skipped
            or CentralDerivativeJobStatus.Quarantined
            or CentralDerivativeJobStatus.TerminalFailure)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("A terminal derivative job cannot be canceled.");
        }
        if (await dbContext.CentralArtifactProcessingEvidence.AnyAsync(
            evidence => evidence.CentralDerivativeJobId == jobId, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException(
                "A derivative job cannot be canceled after output publication has begun.");
        }
        if (job.Status == CentralDerivativeJobStatus.Leased)
        {
            var attemptAffected = await dbContext.CentralDerivativeJobAttempts.Where(attempt =>
                    attempt.CentralDerivativeJobId == job.Id
                    && attempt.AttemptNumber == job.AttemptCount
                    && attempt.Outcome == CentralDerivativeAttemptOutcome.Leased)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(attempt => attempt.Outcome, CentralDerivativeAttemptOutcome.Canceled)
                    .SetProperty(attempt => attempt.ReasonCode, "operation.canceled")
                    .SetProperty(attempt => attempt.EndedAtUtc, now), cancellationToken)
                .ConfigureAwait(false);
            if (attemptAffected != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new CentralDerivativeJobStateException("The active derivative attempt is missing.");
            }
        }
        job.Status = CentralDerivativeJobStatus.Canceled;
        job.CancellationRequestedAtUtc = now;
        job.CancellationRequestedBy = actor;
        job.AvailableAtUtc = null;
        job.UpdatedAtUtc = now;
        job.LeaseOwner = null;
        job.LeaseToken = null;
        job.LeaseAcquiredAtUtc = null;
        job.LeaseExpiresAtUtc = null;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (job.Inputs.Count > 1)
        {
            telemetry.RecordWindowPinDuration(
                job.RecipeName, now - job.Inputs.Min(input => input.SelectedAtUtc), "canceled");
        }
        telemetry.RecordOperation("cancel", "completed");
        Log.Operation(logger, "cancel", actor, jobId, null);
    }

    public async Task RequeueAsync(
        Guid jobId,
        string actor,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? transactionPrecondition = null)
    {
        ValidateActor(actor);
        var now = timeProvider.GetUtcNow();
        var preflightJobArtifacts = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(candidate => candidate.Id == jobId)
            .Select(candidate => new
            {
                candidate.SourceCentralArtifactId,
                candidate.ResultCentralArtifactId,
                candidate.RetainedResultCentralArtifactId
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var preflightArtifactIds = await dbContext.CentralDerivativeJobInputs.AsNoTracking()
            .Where(input => input.CentralDerivativeJobId == jobId)
            .Select(input => input.CentralArtifactId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (preflightJobArtifacts is not null)
        {
            preflightArtifactIds.Add(preflightJobArtifacts.SourceCentralArtifactId);
            if (preflightJobArtifacts.ResultCentralArtifactId.HasValue)
            {
                preflightArtifactIds.Add(preflightJobArtifacts.ResultCentralArtifactId.Value);
            }
            if (preflightJobArtifacts.RetainedResultCentralArtifactId.HasValue)
            {
                preflightArtifactIds.Add(preflightJobArtifacts.RetainedResultCentralArtifactId.Value);
            }
        }
        var holdTargets = await CentralTransientPayloadHoldFence.ReadArtifactsAsync(
            dbContext, preflightArtifactIds, cancellationToken).ConfigureAwait(false);
        await using var holdScope = await CentralTransientPayloadHoldFence.AcquireAsync(
            dbContext, holdTargets, cancellationToken).ConfigureAwait(false);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _ = await CentralDerivativeJobLock.AcquireAsync(dbContext, jobId, cancellationToken).ConfigureAwait(false);
        if (transactionPrecondition is not null
            && !await transactionPrecondition(cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new UnauthorizedAccessException("The transaction precondition no longer permits this job mutation.");
        }
        var artifacts = await dbContext.CentralDerivativeJobs.Where(candidate => candidate.Id == jobId)
            .Select(candidate => new
            {
                SourceId = (Guid?)candidate.SourceCentralArtifactId,
                candidate.ResultCentralArtifactId,
                candidate.RetainedResultCentralArtifactId
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative job does not exist.");
        var sourceId = artifacts.SourceId!.Value;
        var inputIds = await dbContext.CentralDerivativeJobInputs.Where(input => input.CentralDerivativeJobId == jobId)
            .Select(input => input.CentralArtifactId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var lockedArtifactIds = inputIds.Append(sourceId);
        if (artifacts.ResultCentralArtifactId.HasValue)
        {
            lockedArtifactIds = lockedArtifactIds.Append(artifacts.ResultCentralArtifactId.Value);
        }
        if (artifacts.RetainedResultCentralArtifactId.HasValue)
        {
            lockedArtifactIds = lockedArtifactIds.Append(artifacts.RetainedResultCentralArtifactId.Value);
        }
        foreach (var artifactId in lockedArtifactIds.Distinct().Order())
        {
            _ = await CentralArtifactRetentionLock.AcquireAsync(dbContext, artifactId, cancellationToken)
                .ConfigureAwait(false);
        }
        var job = await dbContext.CentralDerivativeJobs
            .Include(candidate => candidate.SourceArtifact)
            .Include(candidate => candidate.InputRequirements)
            .Include(candidate => candidate.Inputs).ThenInclude(input => input.Artifact)
            .Include(candidate => candidate.CanonicalInputs)
            .SingleOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative job does not exist.");
        if (job.Status is not (CentralDerivativeJobStatus.TerminalFailure
            or CentralDerivativeJobStatus.Quarantined
            or CentralDerivativeJobStatus.Canceled
            or CentralDerivativeJobStatus.Skipped))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("Only terminal derivative work can be requeued.");
        }
        if (await dbContext.CentralDerivativeJobs.AnyAsync(candidate => candidate.PredecessorJobId == jobId,
                cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("A derivative job with an active replacement cannot be requeued.");
        }
        if (job.PredecessorJobId.HasValue && await dbContext.CentralDerivativeJobs.AnyAsync(candidate =>
                candidate.Id == job.PredecessorJobId.Value
                && (candidate.Status == CentralDerivativeJobStatus.Pending
                    || candidate.Status == CentralDerivativeJobStatus.Leased
                    || candidate.Status == CentralDerivativeJobStatus.RetryableFailure),
                cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("A replacement cannot be requeued while its predecessor is active.");
        }
        if (job.SourceArtifact?.ObjectState != CentralArtifactObjectState.Available
            || job.SourceArtifact.ReconstructionState != CentralReconstructionState.Complete)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("The derivative source must be repaired before requeue.");
        }
        if (job.RetainedResultCentralArtifactId.HasValue && !await dbContext.CentralArtifacts.AnyAsync(artifact =>
                artifact.Id == job.RetainedResultCentralArtifactId.Value
                && artifact.ObjectState == CentralArtifactObjectState.Available
                && artifact.ReconstructionState == CentralReconstructionState.Complete,
                cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException(
                "The retained derivative result must be repaired before requeue.");
        }
        var unavailableInputs = job.Inputs.Where(input => input.Artifact is not
        {
            ObjectState: CentralArtifactObjectState.Available,
            ReconstructionState: CentralReconstructionState.Complete
        }).ToArray();
        var hasPublishedOutput = await dbContext.CentralArtifactProcessingEvidence.AnyAsync(
            evidence => evidence.CentralDerivativeJobId == job.Id, cancellationToken).ConfigureAwait(false);
        if (hasPublishedOutput && unavailableInputs.Length > 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException(
                "Every published derivative input must be repaired before requeue.");
        }
        if (unavailableInputs.Length > 0)
        {
            dbContext.CentralDerivativeJobInputs.RemoveRange(unavailableInputs);
            foreach (var unavailableInput in unavailableInputs)
            {
                job.Inputs.Remove(unavailableInput);
                var requirement = job.InputRequirements.Single(item => item.Id
                    == unavailableInput.CentralDerivativeJobInputRequirementId);
                requirement.ResolutionState = CentralDerivativeInputResolutionState.Waiting;
                requirement.ResolutionReasonCode = CentralDerivativeJobScheduler.SourceInvalidatedReason;
                requirement.ResolvedAtUtc = null;
            }
            job.InputSetIdentitySha256 = null;
        }
        if (await dbContext.CentralArtifactProcessingEvidence.AnyAsync(evidence =>
                evidence.CentralDerivativeJobId == jobId
                && (evidence.Artifact!.ObjectState != CentralArtifactObjectState.Available
                    || evidence.Artifact.ReconstructionState != CentralReconstructionState.Complete),
                cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException(
                "The unavailable derivative output must be repaired or replaced through reprocessing.");
        }
        var isWindow = job.ResolutionDeadlineUtc.HasValue || job.ResolutionStartedAtUtc.HasValue;
        var needsResolution = isWindow
            && (job.InputSetIdentitySha256 is null
                || job.InputRequirements.Any(requirement => requirement.IsRequired
                    && (requirement.ResolutionState != CentralDerivativeInputResolutionState.Resolved
                        || (requirement.SourceKind == CentralDerivativeInputSourceKind.Artifact
                            ? !job.Inputs.Any(input => input.CentralDerivativeJobInputRequirementId == requirement.Id)
                            : !job.CanonicalInputs.Any(input =>
                                input.CentralDerivativeJobInputRequirementId == requirement.Id)))));
        if (hasPublishedOutput && needsResolution)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException(
                "Published derivative inputs cannot be changed by requeue; create replacement work instead.");
        }
        var reactivatedHoldIds = job.Inputs.Select(input => input.CentralArtifactId)
            .Append(job.SourceCentralArtifactId)
            .Concat(job.RetainedResultCentralArtifactId.HasValue
                ? [job.RetainedResultCentralArtifactId.Value]
                : [])
            .Concat(hasPublishedOutput && job.ResultCentralArtifactId.HasValue
                ? [job.ResultCentralArtifactId.Value]
                : [])
            .ToHashSet();
        var reactivatedHoldTargets = holdScope.Targets.Where(target => reactivatedHoldIds.Contains(target.RecordId))
            .ToArray();
        if (reactivatedHoldTargets.Length != reactivatedHoldIds.Count)
        {
            throw new CentralTransientPayloadHoldRejectedException("transient-retention.hold-target-changed");
        }
        await CentralTransientPayloadHoldFence.ValidateAsync(
            dbContext, reactivatedHoldTargets, cancellationToken).ConfigureAwait(false);
        job.Status = needsResolution ? CentralDerivativeJobStatus.Waiting : CentralDerivativeJobStatus.Pending;
        job.MaxAttempts = checked(job.AttemptCount + CentralDerivativeRecipeCatalog.DefaultMaxAttempts);
        job.AvailableAtUtc = needsResolution ? null : now;
        if (needsResolution)
        {
            var timeoutOrigin = job.ResolutionStartedAtUtc ?? job.CreatedAtUtc;
            var timeout = job.ResolutionDeadlineUtc.HasValue
                ? job.ResolutionDeadlineUtc.Value - timeoutOrigin
                : TimeSpan.FromMinutes(5);
            job.ResolutionDeadlineUtc = now + (timeout > TimeSpan.Zero ? timeout : TimeSpan.FromMinutes(5));
            job.ResolutionStartedAtUtc = now;
            job.ResolutionCompletedAtUtc = null;
            job.InputSetIdentitySha256 = null;
            job.StateReasonCode = CentralDerivativeWindowReasonCodes.WaitingRequiredInput;
            foreach (var requirement in job.InputRequirements)
            {
                var hasInput = requirement.SourceKind == CentralDerivativeInputSourceKind.Artifact
                    ? job.Inputs.Any(input => input.CentralDerivativeJobInputRequirementId == requirement.Id)
                    : job.CanonicalInputs.Any(input => input.CentralDerivativeJobInputRequirementId == requirement.Id);
                if (!hasInput)
                {
                    requirement.ResolutionState = CentralDerivativeInputResolutionState.Waiting;
                    requirement.ResolutionReasonCode = null;
                    requirement.ResolvedAtUtc = null;
                }
            }
        }
        job.UpdatedAtUtc = now;
        job.LastError = null;
        job.CancellationRequestedAtUtc = null;
        job.CancellationRequestedBy = null;
        job.LeaseOwner = null;
        job.LeaseToken = null;
        job.LeaseAcquiredAtUtc = null;
        job.LeaseExpiresAtUtc = null;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordOperation("requeue", "completed");
        Log.Operation(logger, "requeue", actor, jobId, null);
    }

    public async Task<Guid> ReprocessAsync(
        Guid jobId,
        CentralDerivativeReprocessRequest request,
        string actor,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<bool>>? transactionPrecondition = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateActor(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RecipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputVariant);
        var targetRole = GetTargetRole(request.RecipeName);
        var normalizedOptions = BuiltInProcessingRecipes.NormalizeOptions(request.RecipeName, request.Options);
        var selector = ProcessingInputSelector.Raw();
        var requestedIdentity = BuiltInProcessingRecipes.CreateRequestedIdentity(
            request.RecipeName, normalizedOptions, selector).IdentitySha256;
        var now = timeProvider.GetUtcNow();
        var preflightJobArtifacts = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(candidate => candidate.Id == jobId)
            .Select(candidate => new { candidate.SourceCentralArtifactId, candidate.ResultCentralArtifactId })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var preflightArtifactIds = await dbContext.CentralDerivativeJobInputs.AsNoTracking()
            .Where(input => input.CentralDerivativeJobId == jobId)
            .Select(input => input.CentralArtifactId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (preflightJobArtifacts is not null)
        {
            preflightArtifactIds.Add(preflightJobArtifacts.SourceCentralArtifactId);
            if (preflightJobArtifacts.ResultCentralArtifactId.HasValue)
            {
                preflightArtifactIds.Add(preflightJobArtifacts.ResultCentralArtifactId.Value);
            }
        }
        var holdTargets = await CentralTransientPayloadHoldFence.ReadArtifactsAsync(
            dbContext, preflightArtifactIds, cancellationToken).ConfigureAwait(false);
        await using var holdScope = await CentralTransientPayloadHoldFence.AcquireAsync(
            dbContext, holdTargets, cancellationToken).ConfigureAwait(false);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        _ = await CentralDerivativeJobLock.AcquireAsync(dbContext, jobId, cancellationToken).ConfigureAwait(false);
        if (transactionPrecondition is not null
            && !await transactionPrecondition(cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new UnauthorizedAccessException("The transaction precondition no longer permits this job mutation.");
        }
        var artifacts = await dbContext.CentralDerivativeJobs.Where(candidate => candidate.Id == jobId)
            .Select(candidate => new
            {
                SourceId = (Guid?)candidate.SourceCentralArtifactId,
                candidate.ResultCentralArtifactId
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative job does not exist.");
        var sourceId = artifacts.SourceId!.Value;
        var inputIds = await dbContext.CentralDerivativeJobInputs.Where(input => input.CentralDerivativeJobId == jobId)
            .Select(input => input.CentralArtifactId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var lockedArtifactIds = artifacts.ResultCentralArtifactId.HasValue
            ? inputIds.Append(sourceId).Append(artifacts.ResultCentralArtifactId.Value)
            : inputIds.Append(sourceId);
        foreach (var artifactId in lockedArtifactIds.Distinct().Order())
        {
            _ = await CentralArtifactRetentionLock.AcquireAsync(dbContext, artifactId, cancellationToken)
                .ConfigureAwait(false);
        }
        var previous = await dbContext.CentralDerivativeJobs
            .Include(candidate => candidate.SourceArtifact)!.ThenInclude(artifact => artifact!.Frame)
            .Include(candidate => candidate.InputRequirements)
            .Include(candidate => candidate.Inputs).ThenInclude(input => input.Artifact)!.ThenInclude(artifact => artifact!.Frame)
            .SingleOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative job does not exist.");
        if (request.RecipeName == BuiltInProcessingRecipes.RollingMean
            && (previous.RecipeName != BuiltInProcessingRecipes.RollingMean
                || previous.InputSetIdentitySha256 is null
                || previous.InputRequirements.Any(requirement => requirement.IsRequired
                    && (requirement.ResolutionState != CentralDerivativeInputResolutionState.Resolved
                        || !previous.Inputs.Any(input => input.CentralDerivativeJobInputRequirementId == requirement.Id)))
                || requestedIdentity != CentralDerivativeRecipeCatalog.RollingMeanRequestedRecipeIdentity))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException(
                "Rolling reprocessing requires a complete prior rolling window and the canonical window options.");
        }
        if (previous.SourceArtifact?.ObjectState != CentralArtifactObjectState.Available
            || previous.SourceArtifact.ReconstructionState != CentralReconstructionState.Complete)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("The derivative source must be usable for reprocessing.");
        }
        if (previous.ResultCentralArtifactId.HasValue && !await dbContext.CentralArtifacts.AnyAsync(artifact =>
                artifact.Id == previous.ResultCentralArtifactId.Value
                && artifact.ObjectState == CentralArtifactObjectState.Available
                && artifact.ReconstructionState == CentralReconstructionState.Complete,
                cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException(
                "The previous derivative result must be usable while reprocessing retains it.");
        }
        if (previous.Inputs.Any(input => input.Artifact is not
            {
                ObjectState: CentralArtifactObjectState.Available,
                ReconstructionState: CentralReconstructionState.Complete
            }))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException(
                "Every frozen derivative input must be usable for reprocessing.");
        }
        if (request.Supersede && previous.Status is CentralDerivativeJobStatus.Pending
            or CentralDerivativeJobStatus.Leased
            or CentralDerivativeJobStatus.RetryableFailure)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("Active derivative work cannot be superseded.");
        }
        if (previous.TargetRole == targetRole
            && previous.TargetVariant == request.OutputVariant
            && string.Equals(previous.RequestedRecipeIdentitySha256, requestedIdentity, StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            telemetry.RecordOperation(request.Supersede ? "supersede" : "reprocess", "existing");
            return previous.Id;
        }
        var plan = new CentralDerivativeRecipe(
            FrameArtifactRole.Raw,
            targetRole,
            $"reprocess-{requestedIdentity[..16].ToLowerInvariant()}",
            request.OutputVariant,
            request.RecipeName,
            normalizedOptions,
            selector,
            requestedIdentity,
            CentralDerivativeRecipeCatalog.DefaultMaxAttempts);
        var standardRequestIdentity = CentralDerivativeJobIdentity.CreateRequestIdentity(
            previous.SourceArtifact.DevicePublicId!.Value, previous.SourceArtifact.ArtifactId, plan);
        var requestIdentity = standardRequestIdentity;
        var existing = await dbContext.CentralDerivativeJobs
            .SingleOrDefaultAsync(candidate => candidate.RequestIdentitySha256 == requestIdentity, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Id == previous.Id)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                telemetry.RecordOperation(request.Supersede ? "supersede" : "reprocess", "existing");
                return existing.Id;
            }
            if (!request.Supersede)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                telemetry.RecordOperation("reprocess", "existing");
                return existing.Id;
            }
            if (existing.Status == CentralDerivativeJobStatus.Superseded)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new CentralDerivativeJobStateException(
                    "Superseded derivative work cannot become a replacement.");
            }
            if (previous.Status == CentralDerivativeJobStatus.Superseded)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                if (previous.SupersededByJobId != existing.Id)
                {
                    throw new CentralDerivativeJobStateException(
                        "The derivative job is already superseded by different work.");
                }
                telemetry.RecordOperation("supersede", "existing");
                return existing.Id;
            }
            if (existing.PredecessorJobId is not null && existing.PredecessorJobId != previous.Id)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new CentralDerivativeJobStateException(
                    "The derivative replacement is already linked to different predecessor work.");
            }
            if (await dbContext.CentralDerivativeJobs.AnyAsync(candidate => candidate.PredecessorJobId == previous.Id
                    && candidate.Id != existing.Id,
                    cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new CentralDerivativeJobStateException(
                    "The derivative job already has a different designated replacement.");
            }
            if (existing.Status is CentralDerivativeJobStatus.Waiting
                or CentralDerivativeJobStatus.Pending
                or CentralDerivativeJobStatus.Leased
                or CentralDerivativeJobStatus.RetryableFailure
                or CentralDerivativeJobStatus.CancelRequested)
            {
                await CentralTransientPayloadHoldFence.ValidateAsync(
                    dbContext, holdScope.Targets, cancellationToken).ConfigureAwait(false);
            }
            existing.PredecessorJobId = previous.Id;
            existing.RetainedResultCentralArtifactId ??= previous.ResultCentralArtifactId;
            existing.UpdatedAtUtc = now;
            if (existing.Status == CentralDerivativeJobStatus.Completed)
            {
                previous.Status = CentralDerivativeJobStatus.Superseded;
                previous.SupersededByJobId = existing.Id;
                previous.UpdatedAtUtc = now;
            }
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            telemetry.RecordOperation("supersede", "existing");
            Log.Operation(logger, "supersede", actor, jobId, existing.Id);
            return existing.Id;
        }
        if (request.Supersede && await dbContext.CentralDerivativeJobs.AnyAsync(
                candidate => candidate.PredecessorJobId == previous.Id,
                cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException(
                "The derivative job already has a designated replacement.");
        }
        var job = new CentralDerivativeJob
        {
            SourceCentralArtifactId = previous.SourceCentralArtifactId,
            TargetRole = plan.TargetRole,
            TargetRecipeVersion = plan.RecipeVersion,
            TargetVariant = plan.TargetVariant,
            RecipeName = plan.RecipeName,
            RecipeOptionsJson = CaptureContractJson.Canonicalize(plan.Options).GetRawText(),
            InputSelectorJson = CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(plan.InputSelector)).GetRawText(),
            RequestedRecipeIdentitySha256 = plan.RequestedRecipeIdentitySha256,
            ExpectedRecipeIdentitySha256 = plan.RequestedRecipeIdentitySha256,
            RequestIdentitySha256 = requestIdentity,
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString,
            Status = CentralDerivativeJobStatus.Pending,
            ResolutionCompletedAtUtc = now,
            MaxAttempts = plan.MaxAttempts,
            AvailableAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            PredecessorJobId = request.Supersede ? previous.Id : null,
            RetainedResultCentralArtifactId = previous.ResultCentralArtifactId
        };
        var selectedInputs = request.RecipeName == BuiltInProcessingRecipes.RollingMean
            ? previous.Inputs.OrderBy(input => input.Ordinal).ToArray()
            : previous.Inputs.Where(input => input.CentralArtifactId == previous.SourceCentralArtifactId)
                .DefaultIfEmpty(previous.Inputs.OrderBy(input => input.Ordinal).First())
                .Take(1)
                .ToArray();
        if (selectedInputs.Length == 0)
        {
            throw new CentralDerivativeJobStateException("The derivative source set is unavailable for reprocessing.");
        }
        var newHoldArtifactIds = selectedInputs.Select(input => input.CentralArtifactId)
            .Append(previous.SourceCentralArtifactId)
            .Concat(previous.ResultCentralArtifactId.HasValue
                ? [previous.ResultCentralArtifactId.Value]
                : [])
            .ToHashSet();
        var selectedHoldTargets = holdScope.Targets.Where(target => newHoldArtifactIds.Contains(target.RecordId))
            .ToArray();
        if (selectedHoldTargets.Length != newHoldArtifactIds.Count)
        {
            throw new CentralTransientPayloadHoldRejectedException("transient-retention.hold-target-changed");
        }
        await CentralTransientPayloadHoldFence.ValidateAsync(
            dbContext, selectedHoldTargets, cancellationToken).ConfigureAwait(false);
        foreach (var sourceInput in selectedInputs)
        {
            var requirement = new CentralDerivativeJobInputRequirement
            {
                Job = job,
                CentralDerivativeJobId = job.Id,
                Ordinal = job.InputRequirements.Count,
                BindingName = "input",
                SourceKind = CentralDerivativeInputSourceKind.Artifact,
                SequenceOffset = sourceInput.CaptureSequence.HasValue
                    && previous.SourceArtifact.Frame?.CaptureSequence is { } anchorSequence
                    ? checked((int)(sourceInput.CaptureSequence.Value - anchorSequence))
                    : null,
                IsRequired = true,
                SelectorJson = job.InputSelectorJson,
                CompatibilityMode = CentralDerivativeCompatibilityMode.Exact,
                ExpectedAgentId = sourceInput.Artifact!.Frame!.AgentId,
                ExpectedRigId = sourceInput.Artifact.Frame.RigId,
                ExpectedCaptureSequence = sourceInput.CaptureSequence,
                ResolutionState = CentralDerivativeInputResolutionState.Resolved,
                ResolvedAtUtc = now
            };
            job.InputRequirements.Add(requirement);
            job.Inputs.Add(new CentralDerivativeJobInput
            {
                Job = job,
                CentralDerivativeJobId = job.Id,
                Requirement = requirement,
                CentralDerivativeJobInputRequirementId = requirement.Id,
                Ordinal = requirement.Ordinal,
                CentralArtifactId = sourceInput.CentralArtifactId,
                Artifact = sourceInput.Artifact,
                CaptureSequence = sourceInput.CaptureSequence,
                CompatibilityJson = sourceInput.CompatibilityJson,
                CompatibilitySha256 = sourceInput.CompatibilitySha256,
                ByteLength = sourceInput.ByteLength,
                SelectedAtUtc = now
            });
        }
        job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs);
        dbContext.CentralDerivativeJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordOperation(request.Supersede ? "supersede" : "reprocess", "completed");
        Log.Operation(logger, request.Supersede ? "supersede" : "reprocess", actor, jobId, job.Id);
        return job.Id;
    }

    private static FrameArtifactRole GetTargetRole(string recipeName) => recipeName switch
    {
        BuiltInProcessingRecipes.EncodedPreview => FrameArtifactRole.Preview,
        BuiltInProcessingRecipes.Annotation => FrameArtifactRole.AnnotatedPreview,
        BuiltInProcessingRecipes.ImageQuality => FrameArtifactRole.Metadata,
        BuiltInProcessingRecipes.RollingMean => FrameArtifactRole.Combined,
        _ => throw new CentralDerivativeJobStateException("The requested central recipe is unsupported.")
    };

    private static void ValidateActor(string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (actor.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(actor));
        }
    }

    private static partial class Log
    {
        [LoggerMessage(2135, LogLevel.Information,
            "Central derivative operation: Operation={Operation}, Actor={Actor}, JobId={JobId}, NewJobId={NewJobId}")]
        public static partial void Operation(
            ILogger logger,
            string operation,
            string actor,
            Guid jobId,
            Guid? newJobId);
    }
}
