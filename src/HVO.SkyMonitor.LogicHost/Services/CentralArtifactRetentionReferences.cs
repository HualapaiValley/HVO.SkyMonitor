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

    Task<bool> IsHeldOutsideTransientEventAsync(
        Guid centralArtifactId,
        Guid centralTransientEventId,
        CancellationToken cancellationToken);
}

internal interface ICentralArtifactRetentionService
{
    Task<CentralArtifactRetentionResult> ReleaseAsync(Guid centralArtifactId, CancellationToken cancellationToken);
}

internal sealed class CentralArtifactRetentionReferences(ApplicationDbContext dbContext)
    : ICentralArtifactRetentionReferences
{
    public async Task<bool> IsHeldAsync(Guid centralArtifactId, CancellationToken cancellationToken)
    {
        if (await HasCurrentPublicReleaseAsync(centralArtifactId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        if (await dbContext.CentralClearReferenceDesignations.AnyAsync(designation =>
                designation.CentralArtifactId == centralArtifactId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        if (await dbContext.CentralTransientObservations.AnyAsync(observation =>
                observation.Source!.CentralArtifactId == centralArtifactId, cancellationToken).ConfigureAwait(false)
            || await dbContext.CentralTransientObservationBackgrounds.AnyAsync(reference =>
                reference.CentralArtifactId == centralArtifactId, cancellationToken).ConfigureAwait(false)
            || await dbContext.CentralTransientExtractionSources.AnyAsync(reference =>
                reference.CentralArtifactId == centralArtifactId, cancellationToken).ConfigureAwait(false)
            || await dbContext.CentralTransientDerivativeSources.AnyAsync(reference =>
                reference.CentralArtifactId == centralArtifactId, cancellationToken).ConfigureAwait(false)
            || await dbContext.CentralTransientDerivativeBackgrounds.AnyAsync(reference =>
                reference.CentralArtifactId == centralArtifactId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        return await dbContext.CentralDerivativeJobs.AnyAsync(job =>
            (job.SourceCentralArtifactId == centralArtifactId
                || job.Inputs.Any(input => input.CentralArtifactId == centralArtifactId)
                || job.InputRequirements.Any(requirement =>
                    requirement.ExpectedCentralArtifactId == centralArtifactId)
                || job.PredecessorJob!.ResultCentralArtifactId == centralArtifactId
                || job.RetainedResultCentralArtifactId == centralArtifactId
                || dbContext.CentralArtifactProcessingEvidence.Any(evidence =>
                    evidence.CentralArtifactId == centralArtifactId
                    && evidence.CentralDerivativeJobId == job.Id))
            && (job.Status == CentralDerivativeJobStatus.Waiting
                || job.Status == CentralDerivativeJobStatus.Pending
                || job.Status == CentralDerivativeJobStatus.Leased
                || job.Status == CentralDerivativeJobStatus.RetryableFailure
                || job.Status == CentralDerivativeJobStatus.CancelRequested), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> IsHeldOutsideTransientEventAsync(
        Guid centralArtifactId,
        Guid centralTransientEventId,
        CancellationToken cancellationToken)
    {
        if (await HasCurrentPublicReleaseAsync(centralArtifactId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        if (await dbContext.CentralClearReferenceDesignations.AnyAsync(designation =>
                designation.CentralArtifactId == centralArtifactId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }
        if (await dbContext.CentralTransientObservations.AnyAsync(observation =>
                observation.Source!.CentralArtifactId == centralArtifactId &&
                observation.CentralTransientEventId != centralTransientEventId, cancellationToken).ConfigureAwait(false)
            || await dbContext.CentralTransientObservationBackgrounds.AnyAsync(reference =>
                reference.CentralArtifactId == centralArtifactId &&
                reference.Observation!.CentralTransientEventId != centralTransientEventId, cancellationToken)
                .ConfigureAwait(false)
            || await dbContext.CentralTransientExtractionSources.AnyAsync(reference =>
                reference.CentralArtifactId == centralArtifactId &&
                dbContext.CentralTransientValidationIdentitySlots.Any(slot =>
                    slot.CentralDerivativeJobId == reference.CentralDerivativeJobId &&
                    slot.CentralTransientEventId != null &&
                    slot.CentralTransientEventId != centralTransientEventId), cancellationToken).ConfigureAwait(false)
            || await dbContext.CentralTransientDerivativeSources.AnyAsync(reference =>
                reference.CentralArtifactId == centralArtifactId &&
                reference.CentralTransientEventId != centralTransientEventId, cancellationToken).ConfigureAwait(false)
            || await dbContext.CentralTransientDerivativeBackgrounds.AnyAsync(reference =>
                reference.CentralArtifactId == centralArtifactId &&
                reference.CentralTransientEventId != centralTransientEventId, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return await dbContext.CentralDerivativeJobs.AnyAsync(job =>
            (job.SourceCentralArtifactId == centralArtifactId
                || job.Inputs.Any(input => input.CentralArtifactId == centralArtifactId)
                || job.InputRequirements.Any(requirement =>
                    requirement.ExpectedCentralArtifactId == centralArtifactId)
                || job.PredecessorJob!.ResultCentralArtifactId == centralArtifactId
                || job.RetainedResultCentralArtifactId == centralArtifactId
                || dbContext.CentralArtifactProcessingEvidence.Any(evidence =>
                    evidence.CentralArtifactId == centralArtifactId
                    && evidence.CentralDerivativeJobId == job.Id))
            && !dbContext.CentralTransientValidationIdentitySlots.Any(slot =>
                slot.CentralDerivativeJobId == job.Id &&
                slot.CentralTransientEventId == centralTransientEventId)
            && !dbContext.CentralTransientDerivativeJobs.Any(transientJob =>
                transientJob.CentralDerivativeJobId == job.Id &&
                transientJob.CentralTransientEventId == centralTransientEventId)
            && !dbContext.CentralTransientReprocessingJobs.Any(reprocessingJob =>
                reprocessingJob.CentralDerivativeJobId == job.Id &&
                reprocessingJob.CentralTransientEventId == centralTransientEventId)
            && (job.Status == CentralDerivativeJobStatus.Waiting
                || job.Status == CentralDerivativeJobStatus.Pending
                || job.Status == CentralDerivativeJobStatus.Leased
                || job.Status == CentralDerivativeJobStatus.RetryableFailure
                || job.Status == CentralDerivativeJobStatus.CancelRequested), cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> HasCurrentPublicReleaseAsync(
        Guid centralArtifactId,
        CancellationToken cancellationToken)
        => dbContext.PublicRecordPublicationDecisions.AnyAsync(decision =>
            decision.CentralArtifactId == centralArtifactId
            && decision.State == PublicationDecisionState.Released
            && !dbContext.PublicRecordPublicationDecisions.Any(successor =>
                successor.SupersedesDecisionId == decision.Id), cancellationToken);
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
        while (true)
        {
            var storageReference = await dbContext.CentralArtifacts.AsNoTracking()
                .Where(candidate => candidate.Id == centralArtifactId)
                .Select(candidate => candidate.StorageReference)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (storageReference is null)
            {
                telemetry.RecordRetention("not-found");
                return CentralArtifactRetentionResult.NotFound;
            }
            if (!storageReference.StartsWith(BucketPrefix, StringComparison.Ordinal))
            {
                telemetry.RecordRetention("invalid-reference");
                return CentralArtifactRetentionResult.InvalidReference;
            }

            await using var objectLock = await CentralObjectApplicationLock.AcquireAsync(
                dbContext, storageReference, cancellationToken).ConfigureAwait(false);
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
            if (!string.Equals(artifact.StorageReference, storageReference, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                continue;
            }
            if (await references.IsHeldAsync(centralArtifactId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                telemetry.RecordRetention("held");
                return CentralArtifactRetentionResult.Held;
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
