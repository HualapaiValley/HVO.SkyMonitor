using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Data.Common;

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
                || dbContext.CentralTransientContextDependencies.Any(dependency =>
                    dependency.CentralDerivativeJobId == job.Id
                    && dependency.ContextCentralArtifactId == centralArtifactId)
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
                     slot.State != CentralTransientValidationIdentitySlotState.Unused &&
                     (slot.CentralTransientEventId == null ||
                         slot.CentralTransientEventId != centralTransientEventId)), cancellationToken).ConfigureAwait(false)
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
                || dbContext.CentralTransientContextDependencies.Any(dependency =>
                    dependency.CentralDerivativeJobId == job.Id
                    && dependency.ContextCentralArtifactId == centralArtifactId)
                || job.PredecessorJob!.ResultCentralArtifactId == centralArtifactId
                || job.RetainedResultCentralArtifactId == centralArtifactId
                || dbContext.CentralArtifactProcessingEvidence.Any(evidence =>
                    evidence.CentralArtifactId == centralArtifactId
                    && evidence.CentralDerivativeJobId == job.Id))
            && (!dbContext.CentralTransientValidationIdentitySlots.Any(slot =>
                    slot.CentralDerivativeJobId == job.Id &&
                    slot.CentralTransientEventId == centralTransientEventId)
                && !dbContext.CentralTransientDerivativeJobs.Any(transientJob =>
                    transientJob.CentralDerivativeJobId == job.Id &&
                    transientJob.CentralTransientEventId == centralTransientEventId)
                && !dbContext.CentralTransientReprocessingJobs.Any(reprocessingJob =>
                    reprocessingJob.CentralDerivativeJobId == job.Id &&
                    reprocessingJob.CentralTransientEventId == centralTransientEventId)
                 || dbContext.CentralTransientValidationIdentitySlots.Any(slot =>
                     slot.CentralDerivativeJobId == job.Id &&
                     slot.State != CentralTransientValidationIdentitySlotState.Unused &&
                     (slot.CentralTransientEventId == null
                         || slot.CentralTransientEventId != centralTransientEventId))
                || dbContext.CentralTransientDerivativeJobs.Any(transientJob =>
                    transientJob.CentralDerivativeJobId == job.Id &&
                    transientJob.CentralTransientEventId != centralTransientEventId)
                || dbContext.CentralTransientReprocessingJobs.Any(reprocessingJob =>
                    reprocessingJob.CentralDerivativeJobId == job.Id &&
                    reprocessingJob.CentralTransientEventId != centralTransientEventId))
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

internal sealed partial class CentralArtifactRetentionService(
    ApplicationDbContext dbContext,
    ICentralArtifactRetentionReferences references,
    CentralArtifactRetentionProcessor processor,
    TimeProvider timeProvider,
    CentralArtifactRetentionTelemetry telemetry,
    ILogger<CentralArtifactRetentionService> logger) : ICentralArtifactRetentionService
{
    public async Task<CentralArtifactRetentionResult> ReleaseAsync(
        Guid centralArtifactId,
        CancellationToken cancellationToken)
        => await ReleaseCoreAsync(centralArtifactId, cancellationToken).ConfigureAwait(false);

    private async Task<CentralArtifactRetentionResult> ReleaseCoreAsync(
        Guid centralArtifactId,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = CentralArtifactRetentionTelemetry.Start("central-artifact.retention");
        activity?.SetTag("retention.origin", "request");
        while (true)
        {
            var storageReference = await dbContext.CentralArtifacts.AsNoTracking()
                .Where(candidate => candidate.Id == centralArtifactId)
                .Select(candidate => candidate.StorageReference)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (storageReference is null)
            {
                telemetry.RecordOperation("conflict", "request", 0, timeProvider.GetElapsedTime(started));
                return CentralArtifactRetentionResult.NotFound;
            }
            if (!storageReference.StartsWith(CentralObjectOwnershipFence.BucketPrefix, StringComparison.Ordinal)
                || storageReference.Length == CentralObjectOwnershipFence.BucketPrefix.Length)
            {
                telemetry.RecordOperation("failed", "request", 0, timeProvider.GetElapsedTime(started));
                return CentralArtifactRetentionResult.InvalidReference;
            }

            var objectLock = await CentralObjectApplicationLock.AcquireAsync(
                dbContext, storageReference, cancellationToken).ConfigureAwait(false);
            try
            {
                var operationToken = Guid.NewGuid();
                var reserveStarted = timeProvider.GetTimestamp();
                using var reserveActivity = CentralArtifactRetentionTelemetry.Start("central-artifact.retention.reserve");
                ReservationResult reservation;
                try
                {
                    reservation = await ReserveAsync(
                        centralArtifactId, storageReference, operationToken, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsReservationOutcomeAmbiguous(exception))
                {
                    dbContext.ChangeTracker.Clear();
                    if (await ProbeReservationAsync(
                            centralArtifactId, storageReference, operationToken).ConfigureAwait(false))
                    {
                        reserveActivity?.SetTag("retention.outcome", "reserved");
                        return CentralArtifactRetentionResult.Pending;
                    }
                    throw;
                }
                if (reservation.Retry)
                {
                    continue;
                }
                if (reservation.Reserved)
                {
                    var reserveElapsed = timeProvider.GetElapsedTime(reserveStarted);
                    telemetry.RecordStage("reserve", "reserved", "request", reserveElapsed);
                    LogReservation("reserved", "request", "pending", reservation.ByteLength,
                        reserveElapsed.TotalMilliseconds);
                    reserveActivity?.SetTag("retention.outcome", "reserved");
                }
                if (reservation.Result.HasValue)
                {
                    telemetry.RecordOperation(
                        reservation.Result == CentralArtifactRetentionResult.Held ? "held" : "conflict",
                        "request",
                        reservation.ByteLength,
                        timeProvider.GetElapsedTime(started));
                    return reservation.Result.Value;
                }

                try
                {
                    var processResult = await processor.ProcessUnderLockAsync(
                        reservation.DispositionId!.Value, "request", cancellationToken).ConfigureAwait(false);
                    return processResult == CentralArtifactRetentionProcessResult.Released
                        ? CentralArtifactRetentionResult.Released
                        : CentralArtifactRetentionResult.Pending;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return CentralArtifactRetentionResult.Pending;
                }
            }
            finally
            {
                await objectLock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<ReservationResult> ReserveAsync(
        Guid centralArtifactId,
        string storageReference,
        Guid operationToken,
        CancellationToken cancellationToken)
    {
        var objectKey = storageReference[CentralObjectOwnershipFence.BucketPrefix.Length..];
        var identity = CentralObjectOwnershipFence.CreateObjectKeyIdentity(objectKey);
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await CentralArtifactRetentionLock.AcquireAsync(dbContext, centralArtifactId, cancellationToken)
            .ConfigureAwait(false);
        var artifact = await dbContext.CentralArtifacts.SingleOrDefaultAsync(
            candidate => candidate.Id == centralArtifactId, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(CentralArtifactRetentionResult.NotFound, null, 0);
        }
        if (!string.Equals(artifact.StorageReference, storageReference, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            return new(null, null, artifact.ByteLength, Retry: true);
        }

        var identityMatches = await dbContext.CentralObjectRecoveryDispositions
            .Where(item => item.SourceObjectIdentitySha256 == identity)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var disposition = identityMatches.SingleOrDefault(item =>
            string.Equals(item.SourceObjectKey, objectKey, StringComparison.Ordinal));
        if (disposition is null && identityMatches.Count != 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(CentralArtifactRetentionResult.Pending, null, artifact.ByteLength);
        }

        if (artifact.RetentionDeletionToken is { } existingToken)
        {
            if (disposition?.OperationToken != existingToken
                || disposition.CentralArtifactId != artifact.Id
                || disposition.Kind != CentralObjectRecoveryKinds.ExpiredDelete)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new(CentralArtifactRetentionResult.Pending, null, artifact.ByteLength);
            }
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            return disposition.State == CentralObjectRecoveryStates.Completed
                && disposition.CompletedAtUtc.HasValue
                && artifact.RetentionDeletionCompletedAtUtc.HasValue
                && artifact.RetentionDeletionRequestedAtUtc.HasValue
                && disposition.CompletedAtUtc >= artifact.RetentionDeletionRequestedAtUtc
                && artifact.RetentionDeletionCompletedAtUtc >= artifact.RetentionDeletionRequestedAtUtc
                    ? new(CentralArtifactRetentionResult.Released, null, artifact.ByteLength)
                    : new(null, disposition.Id, artifact.ByteLength);
        }

        if (await references.IsHeldAsync(centralArtifactId, cancellationToken).ConfigureAwait(false)
            || await CentralObjectOwnershipFence.HasActiveOwnerAsync(
                dbContext, storageReference, centralArtifactId, cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            return new(CentralArtifactRetentionResult.Held, null, artifact.ByteLength);
        }

        if (disposition is not null
            && (disposition.OperationToken is not null
                || disposition.Kind != CentralObjectRecoveryKinds.ExpiredDelete
                || disposition.State is not (CentralObjectRecoveryStates.PendingDelete
                    or CentralObjectRecoveryStates.Completed
                    or CentralObjectRecoveryStates.Cancelled)))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            return new(CentralArtifactRetentionResult.Pending, null, artifact.ByteLength);
        }

        var now = timeProvider.GetUtcNow();
        artifact.ObjectState = CentralArtifactObjectState.Expired;
        artifact.StateReasonCode = "retention.expired";
        artifact.ReconciledAtUtc = now;
        artifact.RetentionDeletionToken = operationToken;
        artifact.RetentionDeletionRequestedAtUtc = now;
        artifact.RetentionDeletionCompletedAtUtc = null;
        if (disposition is null)
        {
            disposition = new CentralObjectRecoveryDisposition
            {
                SourceObjectIdentitySha256 = identity,
                SourceObjectKey = objectKey,
                Kind = CentralObjectRecoveryKinds.ExpiredDelete,
                State = CentralObjectRecoveryStates.PendingDelete,
                CentralArtifactId = artifact.Id,
                OperationToken = operationToken,
                ByteLength = artifact.ByteLength,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                NextAttemptAtUtc = now
            };
            dbContext.CentralObjectRecoveryDispositions.Add(disposition);
        }
        else
        {
            disposition.Kind = CentralObjectRecoveryKinds.ExpiredDelete;
            disposition.State = CentralObjectRecoveryStates.PendingDelete;
            disposition.CentralArtifactId = artifact.Id;
            disposition.OperationToken = operationToken;
            disposition.ByteLength = artifact.ByteLength;
            disposition.AttemptCount = 0;
            disposition.LastAttemptAtUtc = null;
            disposition.NextAttemptAtUtc = now;
            disposition.CompletedAtUtc = null;
            disposition.ReasonCode = null;
            disposition.UpdatedAtUtc = now;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        return new(null, disposition.Id, artifact.ByteLength, Reserved: true);
    }

    private async Task<bool> ProbeReservationAsync(
        Guid centralArtifactId,
        string storageReference,
        Guid operationToken)
    {
        await using var probe = CreateProbeContext();
        var objectKey = storageReference[CentralObjectOwnershipFence.BucketPrefix.Length..];
        return await probe.CentralArtifacts.AsNoTracking().AnyAsync(artifact =>
                artifact.Id == centralArtifactId
                && artifact.RetentionDeletionToken == operationToken
                && artifact.RetentionDeletionRequestedAtUtc != null
                && artifact.ObjectState == CentralArtifactObjectState.Expired
                && EF.Functions.Collate(artifact.StorageReference, CentralObjectOwnershipFence.BinaryCollation)
                    == storageReference
                && probe.CentralObjectRecoveryDispositions.Any(disposition =>
                    disposition.CentralArtifactId == centralArtifactId
                    && disposition.OperationToken == operationToken
                    && disposition.Kind == CentralObjectRecoveryKinds.ExpiredDelete
                    && EF.Functions.Collate(
                        disposition.SourceObjectKey,
                        CentralObjectOwnershipFence.BinaryCollation) == objectKey),
                CancellationToken.None).ConfigureAwait(false);
    }

    private ApplicationDbContext CreateProbeContext()
    {
        var connectionString = dbContext.Database.GetConnectionString()
            ?? throw new InvalidOperationException("Retention recovery requires SQL Server.");
        return new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connectionString).Options);
    }

    private static bool IsReservationOutcomeAmbiguous(Exception exception)
        => exception is OperationCanceledException or DbException or InvalidOperationException;

    [LoggerMessage(2170, LogLevel.Information,
        "Central artifact retention reservation: Outcome={Outcome} Origin={Origin} State={State} Bytes={Bytes} DurationMs={DurationMs}")]
    private partial void LogReservation(string outcome, string origin, string state, long bytes, double durationMs);

    private sealed record ReservationResult(
        CentralArtifactRetentionResult? Result,
        Guid? DispositionId,
        long ByteLength,
        bool Retry = false,
        bool Reserved = false);
}

internal enum CentralArtifactRetentionResult
{
    Released,
    Held,
    NotFound,
    InvalidReference,
    Pending
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

    public static Task<int> AcquireDispositionAsync(
        ApplicationDbContext dbContext,
        Guid dispositionId,
        CancellationToken cancellationToken)
        => dbContext.Database.SqlQuery<int>(
                $"SELECT CAST(1 AS int) AS [Value] FROM [CentralObjectRecoveryDispositions] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {dispositionId}")
            .SingleOrDefaultAsync(cancellationToken);
}
