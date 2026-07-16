using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using System.Data;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralArtifactRetentionReferences
{
    Task<bool> IsHeldAsync(Guid centralArtifactId, CancellationToken cancellationToken);
}

internal interface ICentralArtifactRetentionService
{
    Task<CentralArtifactRetentionResult> ReleaseAsync(Guid centralArtifactId, CancellationToken cancellationToken);
}

internal sealed class CentralArtifactRetentionReferences(ApplicationDbContext dbContext)
    : ICentralArtifactRetentionReferences
{
    public Task<bool> IsHeldAsync(Guid centralArtifactId, CancellationToken cancellationToken)
        => dbContext.CentralDerivativeJobs.AnyAsync(job =>
            (job.SourceCentralArtifactId == centralArtifactId
                || job.Inputs.Any(input => input.CentralArtifactId == centralArtifactId)
                || job.PredecessorJob!.ResultCentralArtifactId == centralArtifactId
                || job.RetainedResultCentralArtifactId == centralArtifactId
                || dbContext.CentralArtifactProcessingEvidence.Any(evidence =>
                    evidence.CentralArtifactId == centralArtifactId
                    && evidence.CentralDerivativeJobId == job.Id))
            && (job.Status == CentralDerivativeJobStatus.Waiting
                || job.Status == CentralDerivativeJobStatus.Pending
                || job.Status == CentralDerivativeJobStatus.Leased
                || job.Status == CentralDerivativeJobStatus.RetryableFailure
                || job.Status == CentralDerivativeJobStatus.CancelRequested), cancellationToken);
}

internal sealed class CentralArtifactRetentionService(
    ApplicationDbContext dbContext,
    ICentralArtifactRetentionReferences references,
    IMinioClient minio,
    TimeProvider timeProvider,
    CentralArtifactRetrievalTelemetry telemetry) : ICentralArtifactRetentionService
{
    private const string Bucket = "skymonitor-artifacts";
    private const string BucketPrefix = "minio://skymonitor-artifacts/";

    public async Task<CentralArtifactRetentionResult> ReleaseAsync(
        Guid centralArtifactId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await CentralArtifactRetentionLock.AcquireAsync(dbContext, centralArtifactId, cancellationToken)
            .ConfigureAwait(false);
        var artifact = await dbContext.CentralArtifacts.SingleOrDefaultAsync(
            candidate => candidate.Id == centralArtifactId, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            telemetry.RecordRetention("not-found");
            return CentralArtifactRetentionResult.NotFound;
        }
        if (await references.IsHeldAsync(centralArtifactId, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            telemetry.RecordRetention("held");
            return CentralArtifactRetentionResult.Held;
        }
        if (!artifact.StorageReference.StartsWith(BucketPrefix, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            telemetry.RecordRetention("invalid-reference");
            return CentralArtifactRetentionResult.InvalidReference;
        }

        if (artifact.ObjectState != CentralArtifactObjectState.Expired)
        {
            artifact.ObjectState = CentralArtifactObjectState.Expired;
            artifact.StateReasonCode = "retention.expired";
            artifact.ReconciledAtUtc = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        try
        {
            await minio.RemoveObjectAsync(new RemoveObjectArgs()
                .WithBucket(Bucket)
                .WithObject(artifact.StorageReference[BucketPrefix.Length..]), cancellationToken).ConfigureAwait(false);
        }
        catch (MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
        {
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordRetention("released");
        return CentralArtifactRetentionResult.Released;
    }
}

internal enum CentralArtifactRetentionResult
{
    Released,
    Held,
    NotFound,
    InvalidReference
}

internal static class CentralArtifactRetentionLock
{
    public static Task<int> AcquireAsync(
        ApplicationDbContext dbContext,
        Guid centralArtifactId,
        CancellationToken cancellationToken)
        => dbContext.Database.SqlQuery<int>(
                $"SELECT CAST(1 AS int) AS [Value] FROM [CentralArtifacts] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {centralArtifactId}")
            .SingleOrDefaultAsync(cancellationToken);
}
