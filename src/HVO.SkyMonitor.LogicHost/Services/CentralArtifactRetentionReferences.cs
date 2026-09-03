using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
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
        if (await DirectReferences(centralArtifactId).AnyAsync(cancellationToken).ConfigureAwait(false))
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
        if (await DirectReferencesOutsideTransientEvent(centralArtifactId, centralTransientEventId)
                .AnyAsync(cancellationToken).ConfigureAwait(false))
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

    private IQueryable<int> DirectReferences(Guid centralArtifactId)
        => CurrentPublicReleases(centralArtifactId)
            .Concat(dbContext.CentralClearReferenceDesignations
                .Where(item => item.CentralArtifactId == centralArtifactId).Select(_ => 1))
            .Concat(dbContext.CentralTransientObservations
                .Where(item => item.Source!.CentralArtifactId == centralArtifactId).Select(_ => 1))
            .Concat(dbContext.CentralTransientObservationBackgrounds
                .Where(item => item.CentralArtifactId == centralArtifactId).Select(_ => 1))
            .Concat(dbContext.CentralTransientExtractionSources
                .Where(item => item.CentralArtifactId == centralArtifactId).Select(_ => 1))
            .Concat(dbContext.CentralTransientDerivativeSources
                .Where(item => item.CentralArtifactId == centralArtifactId).Select(_ => 1))
            .Concat(dbContext.CentralTransientDerivativeBackgrounds
                .Where(item => item.CentralArtifactId == centralArtifactId).Select(_ => 1))
            .Concat(ActiveGraphExecutionReferences(centralArtifactId));

    private IQueryable<int> DirectReferencesOutsideTransientEvent(
        Guid centralArtifactId,
        Guid centralTransientEventId)
        => CurrentPublicReleases(centralArtifactId)
            .Concat(dbContext.CentralClearReferenceDesignations
                .Where(item => item.CentralArtifactId == centralArtifactId).Select(_ => 1))
            .Concat(dbContext.CentralTransientObservations.Where(item =>
                item.Source!.CentralArtifactId == centralArtifactId
                && item.CentralTransientEventId != centralTransientEventId).Select(_ => 1))
            .Concat(dbContext.CentralTransientObservationBackgrounds.Where(item =>
                item.CentralArtifactId == centralArtifactId
                && item.Observation!.CentralTransientEventId != centralTransientEventId).Select(_ => 1))
            .Concat(dbContext.CentralTransientExtractionSources.Where(item =>
                item.CentralArtifactId == centralArtifactId
                && dbContext.CentralTransientValidationIdentitySlots.Any(slot =>
                    slot.CentralDerivativeJobId == item.CentralDerivativeJobId
                    && slot.State != CentralTransientValidationIdentitySlotState.Unused
                    && (slot.CentralTransientEventId == null
                        || slot.CentralTransientEventId != centralTransientEventId))).Select(_ => 1))
            .Concat(dbContext.CentralTransientDerivativeSources.Where(item =>
                item.CentralArtifactId == centralArtifactId
                && item.CentralTransientEventId != centralTransientEventId).Select(_ => 1))
            .Concat(dbContext.CentralTransientDerivativeBackgrounds.Where(item =>
                item.CentralArtifactId == centralArtifactId
                && item.CentralTransientEventId != centralTransientEventId).Select(_ => 1))
            .Concat(ActiveGraphExecutionReferences(centralArtifactId));

    private IQueryable<int> ActiveGraphExecutionReferences(Guid centralArtifactId)
        => dbContext.CentralProcessingGraphExecutions.Where(execution =>
                (execution.AnchorSourceCentralArtifactId == centralArtifactId ||
                 execution.Sources.Any(source => source.CentralArtifactId == centralArtifactId) ||
                 execution.Jobs.Any(job =>
                     job.SourceCentralArtifactId == centralArtifactId ||
                     job.Inputs.Any(input => input.CentralArtifactId == centralArtifactId) ||
                     job.InputRequirements.Any(requirement =>
                         requirement.ExpectedCentralArtifactId == centralArtifactId) ||
                     job.Outputs.Any(output => output.ResultCentralArtifactId == centralArtifactId))) &&
                (execution.Status == CentralProcessingGraphExecutionStatus.Pending ||
                 execution.Status == CentralProcessingGraphExecutionStatus.Running ||
                 execution.Status == CentralProcessingGraphExecutionStatus.CancelRequested))
            .Select(_ => 1);

    private IQueryable<int> CurrentPublicReleases(Guid centralArtifactId)
        => dbContext.PublicRecordPublicationDecisions.Where(decision =>
                decision.CentralArtifactId == centralArtifactId
                && decision.State == PublicationDecisionState.Released
                && !dbContext.PublicRecordPublicationDecisions.Any(successor =>
                    successor.SupersedesDecisionId == decision.Id))
            .Select(_ => 1);
}

internal sealed partial class CentralArtifactRetentionService(
    ApplicationDbContext dbContext,
    ICentralArtifactRetentionReferences references,
    CentralArtifactRetentionProcessor processor,
    TimeProvider timeProvider,
    CentralArtifactRetentionTelemetry telemetry,
    ILogger<CentralArtifactRetentionService> logger,
    CentralObjectStorageNames? storageNames = null) : ICentralArtifactRetentionService
{
    private readonly CentralObjectStorageNames _storageNames = storageNames ?? new();
    internal const int MaximumReservationConflictRetries = 3;

    internal Func<int, Guid, Exception?>? ReservationFaultInjector { get; set; }

    internal Func<Exception, bool>? ReservationDeadlockClassifier { get; set; }

    internal Func<Exception, bool>? ReservationUniqueConstraintClassifier { get; set; }

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
                telemetry.RecordOperation("not-found", "request", 0, timeProvider.GetElapsedTime(started));
                return CentralArtifactRetentionResult.NotFound;
            }
            if (!storageReference.StartsWith(_storageNames.ArtifactPrefix, StringComparison.Ordinal)
                || storageReference.Length == _storageNames.ArtifactPrefix.Length)
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
                    reservation = await ReserveWithConflictRetryAsync(
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
                        reservation.Result switch
                        {
                            CentralArtifactRetentionResult.Released => "deleted",
                            CentralArtifactRetentionResult.Held => "held",
                            CentralArtifactRetentionResult.NotFound => "not-found",
                            CentralArtifactRetentionResult.Pending => "conflict",
                            _ => "failed"
                        },
                        "request",
                        reservation.ByteLength,
                        timeProvider.GetElapsedTime(started));
                    return reservation.Result.Value;
                }

                try
                {
                    var processResult = reservation.Reserved
                        ? await processor.ProcessPreparedUnderLockAsync(
                            reservation.DispositionId!.Value, "request", cancellationToken).ConfigureAwait(false)
                        : await processor.ProcessUnderLockAsync(
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

    private async Task<ReservationResult> ReserveWithConflictRetryAsync(
        Guid centralArtifactId,
        string storageReference,
        Guid operationToken,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var attemptStarted = timeProvider.GetTimestamp();
            try
            {
                return await ReserveAsync(
                    centralArtifactId,
                    storageReference,
                    operationToken,
                    attempt,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRetryableReservationConflict(exception))
            {
                dbContext.ChangeTracker.Clear();
                if (attempt >= MaximumReservationConflictRetries)
                {
                    throw new InvalidOperationException(
                        "Central artifact retention reservation exhausted SQL conflict retries.",
                        exception);
                }
                telemetry.RecordStage(
                    "reserve", "retry", "request", timeProvider.GetElapsedTime(attemptStarted));
                var delay = TimeSpan.FromMilliseconds(10 * (1 << attempt));
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<ReservationResult> ReserveAsync(
        Guid centralArtifactId,
        string storageReference,
        Guid operationToken,
        int attempt,
        CancellationToken cancellationToken)
    {
        var objectKey = storageReference[_storageNames.ArtifactPrefix.Length..];
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

        var identityMatches = await dbContext.CentralObjectRecoveryDispositions.FromSqlInterpolated($"""
                SELECT *
                FROM [CentralObjectRecoveryDispositions]
                    WITH (READCOMMITTEDLOCK, INDEX([IX_CentralObjectRecoveryDispositions_SourceObjectIdentitySha256]))
                WHERE [SourceObjectIdentitySha256] = {identity}
                """)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var disposition = identityMatches.SingleOrDefault(item =>
            string.Equals(item.SourceObjectKey, objectKey, StringComparison.Ordinal));
        if (disposition is null && identityMatches.Count != 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(CentralArtifactRetentionResult.Pending, null, artifact.ByteLength);
        }
        if (disposition is not null)
        {
            var dispositionId = disposition.Id;
            _ = await CentralArtifactRetentionLock.AcquireDispositionAsync(
                dbContext, dispositionId, cancellationToken).ConfigureAwait(false);
            dbContext.Entry(disposition).State = EntityState.Detached;
            disposition = await dbContext.CentralObjectRecoveryDispositions.SingleOrDefaultAsync(
                item => item.Id == dispositionId, cancellationToken).ConfigureAwait(false);
            if (disposition is null
                || disposition.SourceObjectIdentitySha256 != identity
                || !string.Equals(disposition.SourceObjectKey, objectKey, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                return new(CentralArtifactRetentionResult.Pending, null, artifact.ByteLength);
            }
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

        var reusableCancelledOrphan = disposition is
        {
            Kind: CentralObjectRecoveryKinds.OrphanQuarantine,
            State: CentralObjectRecoveryStates.Cancelled,
            OperationToken: null,
            CentralArtifactId: null
        };
        if (disposition is not null && !reusableCancelledOrphan)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            return new(CentralArtifactRetentionResult.Pending, null, artifact.ByteLength);
        }

        var now = timeProvider.GetUtcNow();
        artifact.ObjectState = CentralArtifactObjectState.Expired;
        artifact.StateReasonCode = "retention.expired";
        artifact.ReconciledAtUtc = now;
        artifact.ObjectVerificationToken = null;
        artifact.ObjectVerificationRequestedAtUtc = null;
        artifact.ObjectVerificationRetryCount = 0;
        artifact.ObjectVerificationRetryAtUtc = null;
        artifact.RetentionDeletionToken = operationToken;
        artifact.RetentionDeletionRequestedAtUtc = now;
        artifact.RetentionDeletionCompletedAtUtc = null;
        disposition ??= new CentralObjectRecoveryDisposition
        {
            SourceObjectIdentitySha256 = identity,
            SourceObjectKey = objectKey,
        };
        disposition.TargetObjectKey = null;
        disposition.Kind = CentralObjectRecoveryKinds.ExpiredDelete;
        disposition.State = CentralObjectRecoveryStates.PendingDelete;
        disposition.CentralArtifactId = artifact.Id;
        disposition.OperationToken = operationToken;
        disposition.ByteLength = artifact.ByteLength;
        disposition.ContentChecksumSha256 = null;
        disposition.AttemptCount = 1;
        disposition.CreatedAtUtc = now;
        disposition.UpdatedAtUtc = now;
        disposition.LastAttemptAtUtc = now;
        disposition.NextAttemptAtUtc = null;
        disposition.CompletedAtUtc = null;
        disposition.ReasonCode = null;
        if (!reusableCancelledOrphan)
        {
            dbContext.CentralObjectRecoveryDispositions.Add(disposition);
        }
        var injectedFault = ReservationFaultInjector?.Invoke(attempt, operationToken);
        if (injectedFault is not null)
        {
            throw injectedFault;
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
        var objectKey = storageReference[_storageNames.ArtifactPrefix.Length..];
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

    private bool IsRetryableReservationConflict(Exception exception)
        => exception is DbUpdateConcurrencyException
            || IsSqlServerDeadlock(exception)
            || ReservationDeadlockClassifier?.Invoke(exception) == true
            || IsSqlServerUniqueConstraintViolation(exception)
            || ReservationUniqueConstraintClassifier?.Invoke(exception) == true;

    internal static bool IsSqlServerUniqueConstraintViolation(Exception exception)
        => ContainsSqlError(exception, 2601, 2627);

    internal static bool IsSqlServerDeadlock(Exception exception)
        => ContainsSqlError(exception, 1205);

    private static bool ContainsSqlError(Exception exception, params int[] errorNumbers)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
            {
                continue;
            }
            if (current is SqlException sqlException
                && (errorNumbers.Contains(sqlException.Number)
                    || sqlException.Errors.Cast<SqlError>().Any(error => errorNumbers.Contains(error.Number))))
            {
                return true;
            }
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
        return false;
    }

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
