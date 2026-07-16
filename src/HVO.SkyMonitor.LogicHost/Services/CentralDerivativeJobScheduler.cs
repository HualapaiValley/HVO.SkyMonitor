using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Diagnostics;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralDerivativeJobScheduler
{
    Task EnsureRequiredJobsAsync(CentralArtifact artifact, DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed class CentralDerivativeJobScheduler(
    ApplicationDbContext dbContext,
    ICentralDerivativeRecipeCatalog recipeCatalog,
    ICentralDerivativeWindowResolver windowResolver) : ICentralDerivativeJobScheduler
{
    internal const string SourceInvalidatedReason = "The derivative source artifact is not usable.";
    internal const string ResultInvalidatedReason = "The derivative result artifact is not usable.";
    internal const string LegacySourceSkippedReason = "The legacy derivative source is not reconstructable.";

    public async Task EnsureRequiredJobsAsync(
        CentralArtifact artifact,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        IDbContextTransaction? ownedTransaction = null;
        try
        {
            if (dbContext.Database.CurrentTransaction is null)
            {
                ownedTransaction = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
            }
            if (ownedTransaction is not null)
            {
                await CentralArtifactRetentionLock.AcquireAsync(dbContext, artifact.Id, cancellationToken)
                    .ConfigureAwait(false);
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (!await dbContext.CentralArtifacts.AsNoTracking().AnyAsync(candidate =>
                    candidate.Id == artifact.Id
                    && candidate.ObjectState == CentralArtifactObjectState.Available
                    && candidate.ReconstructionState == CentralReconstructionState.Complete, cancellationToken)
                    .ConfigureAwait(false))
                {
                    await ownedTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            await EnsureRequiredJobsCoreAsync(artifact, now, cancellationToken).ConfigureAwait(false);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (ownedTransaction is not null)
            {
                await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
                ownedTransaction = null;
                await windowResolver.ResolveAffectedAsync(artifact, now, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            if (ownedTransaction is not null)
            {
                await ownedTransaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task EnsureRequiredJobsCoreAsync(
        CentralArtifact artifact,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var frame = artifact.Frame ?? throw new InvalidOperationException("The artifact frame must be loaded before scheduling derivatives.");
        if (artifact.ObjectState != CentralArtifactObjectState.Available
            || artifact.ReconstructionState != CentralReconstructionState.Complete)
        {
            return;
        }
        if (artifact.Role == FrameArtifactRole.Raw)
        {
            foreach (var recipe in recipeCatalog.GetRequiredRecipes(artifact.Role))
            {
                if (recipe.Window is not null && frame.CaptureSequence is null)
                {
                    continue;
                }
                var requestIdentity = CentralDerivativeJobIdentity.CreateRequestIdentity(
                    frame.DevicePublicId, artifact.ArtifactId, recipe);
                var existing = dbContext.CentralDerivativeJobs.Local.FirstOrDefault(job =>
                    job.RequestIdentitySha256 == requestIdentity)
                    ?? await dbContext.CentralDerivativeJobs.SingleOrDefaultAsync(job =>
                        job.RequestIdentitySha256 == requestIdentity,
                        cancellationToken).ConfigureAwait(false);
                var target = recipe.Window is null ? frame.Artifacts.FirstOrDefault(candidate =>
                    candidate.ManifestSchemaVersion == ArtifactUploadManifest.CurrentSchemaVersion
                    && candidate.Role == recipe.TargetRole
                    && candidate.RecipeVersion == recipe.RecipeVersion
                    && (candidate.Variant ?? string.Empty) == recipe.TargetVariant
                    && IsUsable(candidate)) : null;
                if (target is not null
                    && !await IsCanonicalTargetAsync(artifact, target, recipe, cancellationToken).ConfigureAwait(false))
                {
                    target = null;
                }
                if (existing is null)
                {
                    dbContext.CentralDerivativeJobs.Add(CreateJob(artifact, recipe, target, now));
                }
                else if (target is not null && CanComplete(existing) && !IsInvalidationFailure(existing))
                {
                    Complete(existing, target, now);
                }
                else
                {
                    Restore(existing, artifact, now);
                }
            }
            return;
        }

        var sources = frame.Artifacts.Where(candidate => candidate.Role == FrameArtifactRole.Raw && IsUsable(candidate)).ToArray();
        if (artifact.ManifestSchemaVersion != ArtifactUploadManifest.CurrentSchemaVersion)
        {
            return;
        }
        foreach (var source in sources)
        {
            var recipe = recipeCatalog.GetRequiredRecipes(source.Role).FirstOrDefault(candidate =>
                candidate.TargetRole == artifact.Role
                && candidate.RecipeVersion == artifact.RecipeVersion
                && candidate.TargetVariant == (artifact.Variant ?? string.Empty));
            if (recipe is null)
            {
                continue;
            }
            var canonicalTarget = await IsCanonicalTargetAsync(source, artifact, recipe, cancellationToken)
                .ConfigureAwait(false);
            var requestIdentity = CentralDerivativeJobIdentity.CreateRequestIdentity(
                frame.DevicePublicId, source.ArtifactId, recipe);
            var job = dbContext.CentralDerivativeJobs.Local.FirstOrDefault(candidate =>
                candidate.RequestIdentitySha256 == requestIdentity)
                ?? await dbContext.CentralDerivativeJobs.SingleOrDefaultAsync(candidate =>
                    candidate.RequestIdentitySha256 == requestIdentity,
                    cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                job = CreateJob(source, recipe, canonicalTarget ? artifact : null, now);
                dbContext.CentralDerivativeJobs.Add(job);
            }
            else if (IsInvalidationFailure(job))
            {
                Restore(job, source, now);
            }
            else if (canonicalTarget && CanComplete(job))
            {
                Complete(job, artifact, now);
            }
        }
    }

    private static CentralDerivativeJob CreateJob(
        CentralArtifact source,
        CentralDerivativeRecipe recipe,
        CentralArtifact? result,
        DateTimeOffset now)
    {
        var frame = source.Frame!;
        var isWaiting = result is null && recipe.Window is not null;
        var job = new CentralDerivativeJob
        {
            SourceCentralArtifactId = source.Id,
            SourceArtifact = source,
            TargetRole = recipe.TargetRole,
            TargetRecipeVersion = recipe.RecipeVersion,
            TargetVariant = recipe.TargetVariant,
            RecipeName = recipe.RecipeName,
            RecipeOptionsJson = CaptureContractJson.Canonicalize(recipe.Options).GetRawText(),
            InputSelectorJson = CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(recipe.InputSelector)).GetRawText(),
            RequestedRecipeIdentitySha256 = recipe.RequestedRecipeIdentitySha256,
            RequestIdentitySha256 = CentralDerivativeJobIdentity.CreateRequestIdentity(
                source.Frame!.DevicePublicId, source.ArtifactId, recipe),
            TraceParent = Activity.Current?.Id,
            TraceState = Activity.Current?.TraceStateString,
            Status = result is null
                ? isWaiting ? CentralDerivativeJobStatus.Waiting : CentralDerivativeJobStatus.Pending
                : CentralDerivativeJobStatus.Completed,
            AttemptCount = 0,
            MaxAttempts = recipe.MaxAttempts,
            AvailableAtUtc = result is null && !isWaiting && IsUsable(source) ? now : null,
            ResolutionDeadlineUtc = isWaiting ? now + recipe.Window!.Timeout : null,
            ResolutionStartedAtUtc = isWaiting ? now : null,
            ResolutionCompletedAtUtc = isWaiting ? null : now,
            MissingInputOutcome = recipe.Window?.MissingInputOutcome,
            StateReasonCode = isWaiting ? CentralDerivativeWindowReasonCodes.WaitingRequiredInput : null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CompletedAtUtc = result is null ? null : now,
            ResultCentralArtifactId = result?.Id,
            ResultArtifact = result
        };
        var positions = recipe.Window?.Positions
            ?? [new CentralDerivativeWindowPosition(0, IsRequired: true, recipe.InputSelector)];
        foreach (var position in CentralDerivativeJobIdentity.OrderWindowPositions(positions))
        {
            var requirement = new CentralDerivativeJobInputRequirement
            {
                Job = job,
                CentralDerivativeJobId = job.Id,
                Ordinal = job.InputRequirements.Count,
                BindingName = "input",
                SourceKind = CentralDerivativeInputSourceKind.Artifact,
                SequenceOffset = position.SequenceOffset,
                IsRequired = position.IsRequired,
                SelectorJson = CaptureContractJson.Canonicalize(
                    CaptureContractJson.SerializeToElement(position.Selector)).GetRawText(),
                CompatibilityMode = position.CompatibilityMode,
                ExpectedAgentId = frame.AgentId,
                ExpectedRigId = frame.RigId,
                ExpectedCaptureSequence = AddSequenceOffset(frame.CaptureSequence, position.SequenceOffset),
                ResolutionState = recipe.Window is null
                    ? CentralDerivativeInputResolutionState.Resolved
                    : CentralDerivativeInputResolutionState.Waiting,
                ResolvedAtUtc = recipe.Window is null ? now : null
            };
            job.InputRequirements.Add(requirement);
            if (recipe.Window is null)
            {
                var snapshot = CentralDerivativeWindowCompatibility.EmptySnapshot;
                job.Inputs.Add(new CentralDerivativeJobInput
                {
                    Job = job,
                    CentralDerivativeJobId = job.Id,
                    Requirement = requirement,
                    CentralDerivativeJobInputRequirementId = requirement.Id,
                    Ordinal = requirement.Ordinal,
                    CentralArtifactId = source.Id,
                    Artifact = source,
                    CaptureSequence = frame.CaptureSequence,
                    CompatibilityJson = snapshot.Json,
                    CompatibilitySha256 = snapshot.Sha256,
                    ByteLength = source.ByteLength,
                    SelectedAtUtc = now
                });
            }
        }
        if (recipe.Window is null)
        {
            job.InputSetIdentitySha256 = CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs);
        }
        return job;
    }

    private static long? AddSequenceOffset(long? captureSequence, int sequenceOffset)
    {
        if (!captureSequence.HasValue)
        {
            return null;
        }
        try
        {
            return checked(captureSequence.Value + sequenceOffset);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static void Complete(CentralDerivativeJob job, CentralArtifact result, DateTimeOffset now)
    {
        job.Status = CentralDerivativeJobStatus.Completed;
        job.ResultCentralArtifactId = result.Id;
        job.ResultArtifact = result;
        job.CompletedAtUtc ??= now;
        job.UpdatedAtUtc = now;
        job.AvailableAtUtc = null;
        job.LeaseToken = null;
        job.LeaseOwner = null;
        job.LeaseAcquiredAtUtc = null;
        job.LeaseExpiresAtUtc = null;
        job.LastError = null;
    }

    private static void Restore(CentralDerivativeJob job, CentralArtifact source, DateTimeOffset now)
    {
        if (!IsUsable(source)
            || job.AvailableAtUtc.HasValue
            || job.Status != CentralDerivativeJobStatus.Pending
                && job.Status != CentralDerivativeJobStatus.RetryableFailure
                && job.Status != CentralDerivativeJobStatus.Skipped
            || job.Status == CentralDerivativeJobStatus.RetryableFailure && !IsInvalidationFailure(job)
            || job.Status == CentralDerivativeJobStatus.Skipped
                && !string.Equals(job.LastError, LegacySourceSkippedReason, StringComparison.Ordinal))
        {
            return;
        }
        job.Status = CentralDerivativeJobStatus.Pending;
        if (job.AttemptCount >= job.MaxAttempts)
        {
            job.MaxAttempts = checked(job.AttemptCount + CentralDerivativeRecipeCatalog.DefaultMaxAttempts);
        }
        job.AvailableAtUtc = now;
        job.LastError = null;
        job.UpdatedAtUtc = now;
    }

    private static bool IsInvalidationFailure(CentralDerivativeJob job)
        => string.Equals(job.LastError, SourceInvalidatedReason, StringComparison.Ordinal)
            || string.Equals(job.LastError, ResultInvalidatedReason, StringComparison.Ordinal);

    private async Task<bool> IsCanonicalTargetAsync(
        CentralArtifact source,
        CentralArtifact target,
        CentralDerivativeRecipe recipe,
        CancellationToken cancellationToken)
    {
        var evidence = await dbContext.CentralArtifactProcessingEvidence.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CentralArtifactId == target.Id
                && item.RequestedRecipeIdentitySha256 == recipe.RequestedRecipeIdentitySha256,
                cancellationToken).ConfigureAwait(false);
        if (evidence is null || ProcessingIdentity.CreateArtifactId(evidence.OutputIdentitySha256) != target.ArtifactId)
        {
            return false;
        }
        return await dbContext.CentralArtifactSources.AsNoTracking().AnyAsync(item =>
            item.CentralArtifactId == target.Id
            && item.SourceArtifactId == source.ArtifactId
            && item.ResolvedCentralArtifactId == source.Id,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool CanComplete(CentralDerivativeJob job)
        => job.Status is CentralDerivativeJobStatus.Pending
            or CentralDerivativeJobStatus.RetryableFailure
            or CentralDerivativeJobStatus.Skipped
            or CentralDerivativeJobStatus.Completed;

    private static bool IsUsable(CentralArtifact artifact)
        => artifact.ObjectState == CentralArtifactObjectState.Available
            && artifact.ReconstructionState == CentralReconstructionState.Complete;
}
