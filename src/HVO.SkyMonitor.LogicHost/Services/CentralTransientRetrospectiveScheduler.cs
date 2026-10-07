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
    private const int MaxStaleCameraRequeries = 8;

    /// <summary>The candidate sources taken per query; a test seam, production always uses the default.</summary>
    internal int CandidateBatchSize { get; init; } = BatchSize;

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
        // A camera whose resolved central graph revision this binary cannot expand keeps its sources as candidates
        // with nothing written, so they would fill every batch ahead of newer schedulable sources. Within this pass
        // the batch is re-queried with each such camera excluded, until a batch finds no new one; a restart
        // rediscovers them on its first pass. Resolution is by observatory and logical camera, and a camera belongs
        // to one observatory, so the camera is the key every candidate resolving to that revision shares.
        Dictionary<Guid, CentralProcessingGraphUnexpandableRevision>? staleCameras = null;
        Dictionary<CentralProcessingGraphUnexpandableRevision, int>? deferred = null;
        // A re-query returns again every earlier candidate that is still one; each source is attempted once a pass.
        var considered = new HashSet<(Guid DevicePublicId, Guid ArtifactId)>();
        for (var requery = 0; ; requery++)
        {
            var excludedCameraIds = staleCameras?.Keys.ToArray() ?? [];
            var artifacts = await CreateRetrospectiveCandidateQuery(
                    dbContext, settings.SourceRole, recipe, executionOptionsIdentity)
                .Where(artifact => artifact.Frame!.LogicalCameraInstallationId == null ||
                    !excludedCameraIds.Contains(artifact.Frame.LogicalCameraInstallation!.LogicalCameraId))
                .OrderBy(artifact => artifact.ReceivedAtUtc)
                .ThenBy(artifact => artifact.Id)
                .Select(artifact => new
                {
                    artifact.DevicePublicId,
                    artifact.ArtifactId,
                    LogicalCameraId = (Guid?)artifact.Frame!.LogicalCameraInstallation!.LogicalCameraId
                })
                .Take(CandidateBatchSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var foundStaleCamera = false;
            foreach (var artifact in artifacts)
            {
                if (!considered.Add((artifact.DevicePublicId, artifact.ArtifactId)))
                {
                    continue;
                }
                if (artifact.LogicalCameraId is { } knownCameraId &&
                    staleCameras?.GetValueOrDefault(knownCameraId) is { } known)
                {
                    deferred![known] = deferred.GetValueOrDefault(known) + 1;
                    continue;
                }
                try
                {
                    await scheduler.EnsureRequiredJobsAsync(
                        artifact.DevicePublicId, artifact.ArtifactId, now, cancellationToken).ConfigureAwait(false);
                }
                catch (CentralDerivativeJobStateException exception) when (
                    CentralProcessingGraphPlanVerification.IsUnexpandableRevision(exception, out var unexpandable))
                {
                    // Nothing was written for this source and it stays a candidate for a later pass.
                    dbContext.ChangeTracker.Clear();
                    deferred ??= [];
                    deferred[unexpandable] = deferred.GetValueOrDefault(unexpandable) + 1;
                    if (artifact.LogicalCameraId is { } cameraId)
                    {
                        staleCameras ??= [];
                        foundStaleCamera |= staleCameras.TryAdd(cameraId, unexpandable);
                    }
                }
            }
            if (!foundStaleCamera || artifacts.Count == 0)
            {
                break;
            }
            if (requery == MaxStaleCameraRequeries)
            {
                Log.StaleCameraRequeryLimitReached(logger, MaxStaleCameraRequeries, staleCameras!.Count);
                break;
            }
        }
        if (deferred is not null)
        {
            foreach (var (revision, count) in deferred)
            {
                Log.UnexpandableGraphRevision(logger, count, revision.AssignmentId, revision.RevisionId,
                    revision.UnsupportedNode ?? "(none)", revision.ReasonCode);
            }
        }
        if (considered.Count > 0)
        {
            telemetry.RecordOperation("transient-schedule", "scheduled");
            Log.Scheduled(logger, considered.Count);
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

        [LoggerMessage(2169, LogLevel.Error,
            "Central transient retrospective scheduling deferred {ArtifactCount} source artifacts: central processing " +
            "graph assignment {AssignmentId} resolves revision {RevisionId}, which this binary cannot expand (node " +
            "{NodeAlias}: {ReasonCode}). They are retried on later batches until an operator ends this assignment or " +
            "reassigns a revision published on the current recipe versions.")]
        public static partial void UnexpandableGraphRevision(
            ILogger logger, int artifactCount, Guid? assignmentId, Guid revisionId, string nodeAlias, string reasonCode);

        [LoggerMessage(2177, LogLevel.Warning,
            "Central transient retrospective scheduling stopped after {RequeryCount} re-queries excluding {CameraCount} " +
            "cameras whose central graph revision this binary cannot expand; schedulable sources behind further such " +
            "cameras wait for a later pass.")]
        public static partial void StaleCameraRequeryLimitReached(ILogger logger, int requeryCount, int cameraCount);
    }
}
