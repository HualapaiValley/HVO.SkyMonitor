using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralDerivativeJobScheduler
{
    Task EnsureRequiredJobsAsync(CentralArtifact artifact, DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed class CentralDerivativeJobScheduler(
    ApplicationDbContext dbContext,
    ICentralDerivativeRecipeCatalog recipeCatalog) : ICentralDerivativeJobScheduler
{
    public async Task EnsureRequiredJobsAsync(
        CentralArtifact artifact,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var frame = artifact.Frame ?? throw new InvalidOperationException("The artifact frame must be loaded before scheduling derivatives.");
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
                    candidate.Role == recipe.TargetRole && candidate.RecipeVersion == recipe.RecipeVersion);
                if (existing is null)
                {
                    dbContext.CentralDerivativeJobs.Add(CreateJob(artifact, recipe, target, now));
                }
                else if (target is not null)
                {
                    Complete(existing, target, now);
                }
            }
            return;
        }

        var sources = frame.Artifacts.Where(candidate => candidate.Role == FrameArtifactRole.Raw).ToArray();
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
            AvailableAtUtc = result is null ? now : null,
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
    }
}
