using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralDerivativeJobScheduler
{
    Task EnsureRequiredJobsAsync(CentralArtifact artifact, DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed class CentralDerivativeJobScheduler(
    ApplicationDbContext dbContext,
    ICentralDerivativeRecipeCatalog recipeCatalog) : ICentralDerivativeJobScheduler
{
    internal const string SourceInvalidatedReason = "The derivative source artifact is not usable.";
    internal const string ResultInvalidatedReason = "The derivative result artifact is not usable.";

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
                    && (candidate.ReconstructionState == CentralReconstructionState.Complete
                        || candidate.ReconstructionState == CentralReconstructionState.LegacyIncomplete), cancellationToken)
                    .ConfigureAwait(false))
                {
                    await ownedTransaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            await EnsureRequiredJobsCoreAsync(artifact, now, cancellationToken).ConfigureAwait(false);
            if (ownedTransaction is not null)
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await ownedTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
            || artifact.ReconstructionState is not (CentralReconstructionState.Complete or CentralReconstructionState.LegacyIncomplete))
        {
            return;
        }
        if (artifact.Role == FrameArtifactRole.Raw)
        {
            foreach (var recipe in recipeCatalog.GetRequiredRecipes(artifact.Role))
            {
                var existing = dbContext.CentralDerivativeJobs.Local.FirstOrDefault(job =>
                    job.SourceCentralArtifactId == artifact.Id
                    && job.TargetRole == recipe.TargetRole
                    && job.TargetRecipeVersion == recipe.RecipeVersion)
                    ?? await dbContext.CentralDerivativeJobs.SingleOrDefaultAsync(job =>
                        job.SourceCentralArtifactId == artifact.Id
                        && job.TargetRole == recipe.TargetRole
                        && job.TargetRecipeVersion == recipe.RecipeVersion,
                        cancellationToken).ConfigureAwait(false);
                var target = frame.Artifacts.FirstOrDefault(candidate =>
                    candidate.ManifestSchemaVersion == ArtifactUploadManifest.CurrentSchemaVersion
                    && candidate.Role == recipe.TargetRole
                    && candidate.RecipeVersion == recipe.RecipeVersion
                    && IsUsable(candidate));
                if (existing is null)
                {
                    dbContext.CentralDerivativeJobs.Add(CreateJob(artifact, recipe, target, now));
                }
                else if (target is not null && !IsInvalidationFailure(existing))
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
                candidate.TargetRole == artifact.Role && candidate.RecipeVersion == artifact.RecipeVersion);
            if (recipe is null)
            {
                continue;
            }
            var job = dbContext.CentralDerivativeJobs.Local.FirstOrDefault(candidate =>
                candidate.SourceCentralArtifactId == source.Id
                && candidate.TargetRole == recipe.TargetRole
                && candidate.TargetRecipeVersion == recipe.RecipeVersion)
                ?? await dbContext.CentralDerivativeJobs.SingleOrDefaultAsync(candidate =>
                    candidate.SourceCentralArtifactId == source.Id
                    && candidate.TargetRole == recipe.TargetRole
                    && candidate.TargetRecipeVersion == recipe.RecipeVersion,
                    cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                job = CreateJob(source, recipe, artifact, now);
                dbContext.CentralDerivativeJobs.Add(job);
            }
            else if (IsInvalidationFailure(job))
            {
                Restore(job, source, now);
            }
            else
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
        => new()
        {
            SourceCentralArtifactId = source.Id,
            SourceArtifact = source,
            TargetRole = recipe.TargetRole,
            TargetRecipeVersion = recipe.RecipeVersion,
            Status = result is null ? CentralDerivativeJobStatus.Pending : CentralDerivativeJobStatus.Completed,
            AttemptCount = 0,
            MaxAttempts = recipe.MaxAttempts,
            AvailableAtUtc = result is null && IsUsable(source) ? now : null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CompletedAtUtc = result is null ? null : now,
            ResultCentralArtifactId = result?.Id,
            ResultArtifact = result
        };

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
                && (job.Status != CentralDerivativeJobStatus.RetryableFailure
                    || !IsInvalidationFailure(job)))
        {
            return;
        }
        job.Status = CentralDerivativeJobStatus.Pending;
        job.AttemptCount = 0;
        job.AvailableAtUtc = now;
        job.LastError = null;
        job.UpdatedAtUtc = now;
    }

    private static bool IsInvalidationFailure(CentralDerivativeJob job)
        => string.Equals(job.LastError, SourceInvalidatedReason, StringComparison.Ordinal)
            || string.Equals(job.LastError, ResultInvalidatedReason, StringComparison.Ordinal);

    private static bool IsUsable(CentralArtifact artifact)
        => artifact.ObjectState == CentralArtifactObjectState.Available
            && artifact.ReconstructionState is CentralReconstructionState.Complete
                or CentralReconstructionState.LegacyIncomplete;
}
