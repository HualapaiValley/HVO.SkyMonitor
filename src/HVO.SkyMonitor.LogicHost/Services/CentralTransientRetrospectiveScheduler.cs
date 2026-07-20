using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralTransientRetrospectiveScheduler
{
    Task ScheduleBatchAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

internal sealed partial class CentralTransientRetrospectiveScheduler(
    ApplicationDbContext dbContext,
    ICentralDerivativeJobScheduler scheduler,
    ICentralDerivativeRecipeCatalog recipeCatalog,
    IOptions<CentralTransientOptions> options,
    CentralDerivativeWorkerTelemetry telemetry,
    ILogger<CentralTransientRetrospectiveScheduler> logger) : ICentralTransientRetrospectiveScheduler
{
    private const int BatchSize = 100;

    public async Task ScheduleBatchAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.Mode != TransientDetectorExecutionMode.Central)
        {
            return;
        }
        var recipe = recipeCatalog.GetRequiredRecipes(settings.SourceRole).Single(item =>
            string.Equals(item.RecipeName, CentralTransientRuntime.RecipeName, StringComparison.Ordinal));
        var executionOptionsIdentity = recipe.Transient?.ExecutionOptionsIdentitySha256
            ?? throw new InvalidOperationException("The central transient recipe is missing its execution policy.");
        var provisionalJobIds = await dbContext.CentralTransientValidationJobs.AsNoTracking()
            .Where(item => item.OutcomeRecordedAtUtc != null && item.ContextDependencies.Any() &&
                item.ExecutionOptionsIdentitySha256 == executionOptionsIdentity &&
                !dbContext.CentralTransientValidationJobs.Any(successor =>
                    successor.ProvisionalCentralDerivativeJobId == item.CentralDerivativeJobId))
            .OrderBy(item => item.OutcomeRecordedAtUtc)
            .Select(item => item.CentralDerivativeJobId)
            .Take(BatchSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var provisionalJobId in provisionalJobIds)
        {
            _ = await scheduler.EnsureTransientContextConvergenceAsync(
                provisionalJobId, now, cancellationToken).ConfigureAwait(false);
        }
        dbContext.ChangeTracker.Clear();
        var artifacts = await CreateRetrospectiveCandidateQuery(
                dbContext, settings.SourceRole, recipe, executionOptionsIdentity)
            .OrderBy(artifact => artifact.ReceivedAtUtc)
            .ThenBy(artifact => artifact.Id)
            .Select(artifact => new { artifact.DevicePublicId, artifact.ArtifactId })
            .Take(BatchSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var artifact in artifacts)
        {
            await scheduler.EnsureRequiredJobsAsync(
                artifact.DevicePublicId!.Value, artifact.ArtifactId, now, cancellationToken).ConfigureAwait(false);
        }
        if (artifacts.Count > 0)
        {
            telemetry.RecordOperation("transient-schedule", "scheduled");
            Log.Scheduled(logger, artifacts.Count);
        }
    }

    internal static IQueryable<CentralArtifact> CreateRetrospectiveCandidateQuery(
        ApplicationDbContext dbContext,
        FrameArtifactRole sourceRole,
        CentralDerivativeRecipe recipe,
        string executionOptionsIdentity)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionOptionsIdentity);
        return dbContext.CentralArtifacts.AsNoTracking()
            .Where(artifact => artifact.Role == sourceRole
                && artifact.DevicePublicId != null
                && artifact.ObjectState == CentralArtifactObjectState.Available
                && artifact.ReconstructionState == CentralReconstructionState.Complete
                && artifact.Frame!.CaptureSequence != null
                && !dbContext.CentralDerivativeJobs.Any(job => job.SourceCentralArtifactId == artifact.Id &&
                    job.RecipeName == recipe.RecipeName &&
                    job.RequestedRecipeIdentitySha256 == recipe.RequestedRecipeIdentitySha256 &&
                    job.TargetRole == recipe.TargetRole &&
                    job.TargetRecipeVersion == recipe.RecipeVersion &&
                    job.TargetVariant == recipe.TargetVariant &&
                    dbContext.CentralTransientValidationJobs.Any(validation =>
                        validation.CentralDerivativeJobId == job.Id &&
                        validation.ExecutionOptionsIdentitySha256 == executionOptionsIdentity)));
    }

    private static partial class Log
    {
        [LoggerMessage(2160, LogLevel.Information,
            "Central transient retrospective scheduling considered {ArtifactCount} source artifacts.")]
        public static partial void Scheduled(ILogger logger, int artifactCount);
    }
}
