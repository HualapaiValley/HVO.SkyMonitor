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
    Task CancelAsync(Guid jobId, string actor, CancellationToken cancellationToken);

    Task RequeueAsync(Guid jobId, string actor, CancellationToken cancellationToken);

    Task<Guid> ReprocessAsync(
        Guid jobId,
        CentralDerivativeReprocessRequest request,
        string actor,
        CancellationToken cancellationToken);
}

internal sealed partial class CentralDerivativeJobOperationsService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    CentralDerivativeWorkerTelemetry telemetry,
    ILogger<CentralDerivativeJobOperationsService> logger) : ICentralDerivativeJobOperationsService
{
    public async Task CancelAsync(Guid jobId, string actor, CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _ = await CentralDerivativeJobLock.AcquireAsync(dbContext, jobId, cancellationToken).ConfigureAwait(false);
        var job = await dbContext.CentralDerivativeJobs.SingleOrDefaultAsync(
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
        telemetry.RecordOperation("cancel", "completed");
        Log.Operation(logger, "cancel", actor, jobId, null);
    }

    public async Task RequeueAsync(Guid jobId, string actor, CancellationToken cancellationToken)
    {
        ValidateActor(actor);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var job = await dbContext.CentralDerivativeJobs
            .Include(candidate => candidate.SourceArtifact)
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
        if (job.SourceArtifact?.ObjectState != CentralArtifactObjectState.Available
            || job.SourceArtifact.ReconstructionState != CentralReconstructionState.Complete)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("The derivative source must be repaired before requeue.");
        }
        if (await dbContext.CentralArtifactProcessingEvidence.AnyAsync(evidence =>
                evidence.CentralDerivativeJobId == jobId
                && evidence.Artifact!.ObjectState == CentralArtifactObjectState.Quarantined,
                cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException(
                "The quarantined derivative output must be repaired or replaced through reprocessing.");
        }
        job.Status = CentralDerivativeJobStatus.Pending;
        job.MaxAttempts = checked(job.AttemptCount + CentralDerivativeRecipeCatalog.DefaultMaxAttempts);
        job.AvailableAtUtc = now;
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
        CancellationToken cancellationToken)
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
        var sourceId = await dbContext.CentralDerivativeJobs.Where(candidate => candidate.Id == jobId)
            .Select(candidate => (Guid?)candidate.SourceCentralArtifactId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative job does not exist.");
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        _ = await CentralArtifactRetentionLock.AcquireAsync(dbContext, sourceId, cancellationToken).ConfigureAwait(false);
        var previous = await dbContext.CentralDerivativeJobs
            .Include(candidate => candidate.SourceArtifact)
            .SingleOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative job does not exist.");
        if (previous.SourceArtifact?.ObjectState != CentralArtifactObjectState.Available
            || previous.SourceArtifact.ReconstructionState != CentralReconstructionState.Complete)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("The derivative source must be usable for reprocessing.");
        }
        if (request.Supersede && previous.Status is CentralDerivativeJobStatus.Pending
            or CentralDerivativeJobStatus.Leased
            or CentralDerivativeJobStatus.RetryableFailure)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("Active derivative work cannot be superseded.");
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
        var requestIdentity = CentralDerivativeJobIdentity.CreateRequestIdentity(
            previous.SourceArtifact.DevicePublicId!.Value, previous.SourceArtifact.ArtifactId, plan);
        var existing = await dbContext.CentralDerivativeJobs.AsNoTracking()
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
            previous.Status = CentralDerivativeJobStatus.Superseded;
            previous.SupersededByJobId = existing.Id;
            previous.UpdatedAtUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            telemetry.RecordOperation("supersede", "completed");
            Log.Operation(logger, "supersede", actor, jobId, existing.Id);
            return existing.Id;
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
            RequestIdentitySha256 = requestIdentity,
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString,
            Status = CentralDerivativeJobStatus.Pending,
            MaxAttempts = plan.MaxAttempts,
            AvailableAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        dbContext.CentralDerivativeJobs.Add(job);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (request.Supersede)
        {
            previous.Status = CentralDerivativeJobStatus.Superseded;
            previous.SupersededByJobId = job.Id;
            previous.UpdatedAtUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
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
