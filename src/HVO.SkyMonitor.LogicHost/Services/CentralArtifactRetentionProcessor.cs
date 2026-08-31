using System.Data;
using System.Data.Common;
using System.Diagnostics;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralArtifactRetentionProcessor
{
    Task<CentralArtifactRetentionProcessResult> ProcessAsync(
        Guid dispositionId,
        string origin,
        CancellationToken cancellationToken);
}

internal enum CentralArtifactRetentionProcessResult
{
    Released,
    Pending
}

internal sealed partial class CentralArtifactRetentionProcessor(
    ApplicationDbContext dbContext,
    ICentralArtifactRetentionReferences references,
    IObjectStore objectStore,
    TimeProvider timeProvider,
    CentralArtifactRetentionTelemetry telemetry,
    ILogger<CentralArtifactRetentionProcessor> logger,
    CentralObjectStorageNames? storageNames = null) : ICentralArtifactRetentionProcessor
{
    private readonly CentralObjectStorageNames _storageNames = storageNames ?? new();
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(5);
    private const int MaximumSqlDeadlockRetries = 3;

    internal Func<Exception, bool>? SqlDeadlockClassifier { get; set; }

    public async Task<CentralArtifactRetentionProcessResult> ProcessAsync(
        Guid dispositionId,
        string origin,
        CancellationToken cancellationToken)
    {
        using var activity = CentralArtifactRetentionTelemetry.Start("central-artifact.retention");
        activity?.SetTag("retention.origin", origin);
        var storageReference = await dbContext.CentralObjectRecoveryDispositions.AsNoTracking()
            .Where(item => item.Id == dispositionId && item.OperationToken != null)
            .Select(item => _storageNames.ArtifactPrefix + item.SourceObjectKey)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (storageReference is null)
        {
            return CentralArtifactRetentionProcessResult.Pending;
        }

        await using var objectLock = await CentralObjectApplicationLock.AcquireAsync(
            dbContext, storageReference, cancellationToken).ConfigureAwait(false);
        return await ProcessUnderLockAsync(dispositionId, origin, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CentralArtifactRetentionProcessResult> ProcessUnderLockAsync(
        Guid dispositionId,
        string origin,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var activity = Activity.Current;
        var snapshot = await LoadSnapshotAsync(dispositionId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            await PersistInvalidLinkedStateAsync(dispositionId, cancellationToken).ConfigureAwait(false);
            telemetry.RecordFinalizationConflict(origin);
            activity?.SetTag("retention.outcome", "conflict");
            return CentralArtifactRetentionProcessResult.Pending;
        }
        if (snapshot.State == CentralObjectRecoveryStates.Failed)
        {
            activity?.SetTag("retention.outcome", "failed");
            return CentralArtifactRetentionProcessResult.Pending;
        }
        if (snapshot.State == CentralObjectRecoveryStates.Completed)
        {
            var completed = IsDurablyCompleted(snapshot);
            activity?.SetTag("retention.outcome", completed ? "deleted" : "conflict");
            return completed
                ? CentralArtifactRetentionProcessResult.Released
                : CentralArtifactRetentionProcessResult.Pending;
        }
        if (snapshot.NextAttemptAtUtc > timeProvider.GetUtcNow())
        {
            activity?.SetTag("retention.outcome", "retry");
            return CentralArtifactRetentionProcessResult.Pending;
        }

        snapshot = await ExecuteSqlDeadlockRetryAsync(
            () => PrepareDeleteAsync(snapshot, cancellationToken),
            "reconcile",
            origin,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            telemetry.RecordFinalizationConflict(origin);
            return CentralArtifactRetentionProcessResult.Pending;
        }

        return await DeleteAndFinalizeAsync(snapshot, origin, started, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<CentralArtifactRetentionProcessResult> ProcessPreparedUnderLockAsync(
        Guid dispositionId,
        string origin,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var snapshot = await LoadSnapshotAsync(dispositionId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null
            || snapshot.State != CentralObjectRecoveryStates.PendingDelete
            || snapshot.ArtifactState != CentralArtifactObjectState.Expired
            || !snapshot.ArtifactRequestedAtUtc.HasValue
            || snapshot.ArtifactCompletedAtUtc.HasValue
            || snapshot.DispositionCompletedAtUtc.HasValue
            || snapshot.AttemptCount != 1
            || !snapshot.LastAttemptAtUtc.HasValue
            || snapshot.NextAttemptAtUtc.HasValue)
        {
            telemetry.RecordFinalizationConflict(origin);
            return CentralArtifactRetentionProcessResult.Pending;
        }

        return await DeleteAndFinalizeAsync(snapshot, origin, started, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CentralArtifactRetentionProcessResult> DeleteAndFinalizeAsync(
        RetentionSnapshot snapshot,
        string origin,
        long started,
        CancellationToken cancellationToken)
    {
        var activity = Activity.Current;

        var deleteStarted = timeProvider.GetTimestamp();
        var deleteOutcome = "deleted";
        using var deleteActivity = CentralArtifactRetentionTelemetry.Start("central-artifact.retention.delete");
        try
        {
            await objectStore.DeleteAsync(
                _storageNames.ArtifactBucket, snapshot.ObjectKey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.RecordStage("delete", "retry", origin, timeProvider.GetElapsedTime(deleteStarted));
            LogDelete("retry", origin, "cancelled", snapshot.ByteLength,
                timeProvider.GetElapsedTime(deleteStarted).TotalMilliseconds);
            telemetry.RecordOperation("retry", origin, snapshot.ByteLength, timeProvider.GetElapsedTime(started));
            deleteActivity?.SetTag("retention.outcome", "retry");
            activity?.SetTag("retention.outcome", "retry");
            return CentralArtifactRetentionProcessResult.Pending;
        }
        catch (Exception exception) when (IsObjectStoreFailure(exception))
        {
            var failureCategory = GetFailureCategory(exception);
            var terminal = IsTerminal(exception);
            await PersistFailureAsync(snapshot, failureCategory, terminal).ConfigureAwait(false);
            var outcome = terminal ? "terminal" : "retry";
            telemetry.RecordStage("delete", outcome, origin, timeProvider.GetElapsedTime(deleteStarted));
            LogDelete(outcome, origin, failureCategory, snapshot.ByteLength,
                timeProvider.GetElapsedTime(deleteStarted).TotalMilliseconds);
            if (terminal)
            {
                LogTerminalFailure("failed", origin, failureCategory, snapshot.ByteLength);
            }
            telemetry.RecordOperation(outcome, origin, snapshot.ByteLength, timeProvider.GetElapsedTime(started));
            deleteActivity?.SetTag("retention.outcome", outcome);
            activity?.SetTag("retention.outcome", outcome);
            return CentralArtifactRetentionProcessResult.Pending;
        }

        deleteActivity?.SetTag("retention.outcome", deleteOutcome);
        telemetry.RecordStage("delete", deleteOutcome, origin, timeProvider.GetElapsedTime(deleteStarted));
        LogDelete(deleteOutcome, origin, "none", snapshot.ByteLength,
            timeProvider.GetElapsedTime(deleteStarted).TotalMilliseconds);
        var finalized = await ExecuteSqlDeadlockRetryAsync(
            () => TryFinalizeAsync(snapshot, origin, cancellationToken),
            "finalize",
            origin,
            cancellationToken).ConfigureAwait(false);
        var elapsed = timeProvider.GetElapsedTime(started);
        telemetry.RecordOperation(finalized ? deleteOutcome : "conflict", origin, snapshot.ByteLength, elapsed);
        activity?.SetTag("retention.outcome", finalized ? deleteOutcome : "conflict");
        return finalized ? CentralArtifactRetentionProcessResult.Released : CentralArtifactRetentionProcessResult.Pending;
    }

    private async Task<T> ExecuteSqlDeadlockRetryAsync<T>(
        Func<Task<T>> operation,
        string stage,
        string origin,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var attemptStarted = timeProvider.GetTimestamp();
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception exception) when (IsSqlDeadlock(exception))
            {
                dbContext.ChangeTracker.Clear();
                if (attempt >= MaximumSqlDeadlockRetries)
                {
                    throw new InvalidOperationException(
                        $"Central artifact retention {stage} exhausted SQL deadlock retries.",
                        exception);
                }
                telemetry.RecordStage(stage, "retry", origin, timeProvider.GetElapsedTime(attemptStarted));
                var delay = TimeSpan.FromMilliseconds(10 * (1 << attempt));
                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<RetentionSnapshot?> PrepareDeleteAsync(
        RetentionSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        _ = await CentralArtifactRetentionLock.AcquireAsync(
            dbContext, snapshot.CentralArtifactId, cancellationToken).ConfigureAwait(false);
        _ = await CentralArtifactRetentionLock.AcquireDispositionAsync(
            dbContext, snapshot.DispositionId, cancellationToken).ConfigureAwait(false);
        var valid = await LoadSnapshotQuery(snapshot.DispositionId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        if (valid is null
            || valid.State != CentralObjectRecoveryStates.PendingDelete
            || valid.OperationToken != snapshot.OperationToken
            || valid.CentralArtifactId != snapshot.CentralArtifactId
            || valid.ArtifactState != CentralArtifactObjectState.Expired
            || !valid.ArtifactRequestedAtUtc.HasValue
            || valid.ArtifactCompletedAtUtc.HasValue
            || valid.DispositionCompletedAtUtc.HasValue
            || valid.NextAttemptAtUtc > now)
        {
            var current = await dbContext.CentralObjectRecoveryDispositions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == snapshot.DispositionId, cancellationToken)
                .ConfigureAwait(false);
            if (current is not null
                && current.OperationToken is not null
                && current.State == CentralObjectRecoveryStates.PendingDelete)
            {
                _ = await dbContext.CentralObjectRecoveryDispositions.Where(item =>
                        item.Id == current.Id
                        && item.OperationToken == current.OperationToken
                        && item.State == CentralObjectRecoveryStates.PendingDelete
                        && item.RowVersion == current.RowVersion)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(item => item.State, CentralObjectRecoveryStates.Failed)
                        .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1)
                        .SetProperty(item => item.LastAttemptAtUtc, now)
                        .SetProperty(item => item.NextAttemptAtUtc, (DateTimeOffset?)null)
                        .SetProperty(item => item.CompletedAtUtc, (DateTimeOffset?)null)
                        .SetProperty(item => item.ReasonCode, "retention.state-conflict")
                        .SetProperty(item => item.UpdatedAtUtc, now), cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            dbContext.ChangeTracker.Clear();
            return null;
        }

        var affected = await dbContext.CentralObjectRecoveryDispositions.Where(item =>
                item.Id == valid.DispositionId
                && item.CentralArtifactId == valid.CentralArtifactId
                && item.OperationToken == valid.OperationToken
                && item.State == CentralObjectRecoveryStates.PendingDelete
                && item.RowVersion == valid.DispositionRowVersion
                && EF.Functions.Collate(item.SourceObjectKey, CentralObjectOwnershipFence.BinaryCollation)
                    == valid.ObjectKey
                && dbContext.CentralArtifacts.Any(artifact =>
                    artifact.Id == valid.CentralArtifactId
                    && artifact.RetentionDeletionToken == valid.OperationToken
                    && artifact.ObjectState == CentralArtifactObjectState.Expired
                    && artifact.RetentionDeletionRequestedAtUtc == valid.ArtifactRequestedAtUtc
                    && artifact.RetentionDeletionCompletedAtUtc == null
                    && artifact.RowVersion == valid.ArtifactRowVersion
                    && EF.Functions.Collate(artifact.StorageReference, CentralObjectOwnershipFence.BinaryCollation)
                        == _storageNames.ArtifactPrefix + valid.ObjectKey))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1)
                .SetProperty(item => item.LastAttemptAtUtc, now)
                .SetProperty(item => item.NextAttemptAtUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.UpdatedAtUtc, now), cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            return null;
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        return await LoadSnapshotAsync(snapshot.DispositionId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryFinalizeAsync(
        RetentionSnapshot snapshot,
        string origin,
        CancellationToken cancellationToken)
    {
        using var activity = CentralArtifactRetentionTelemetry.Start("central-artifact.retention.finalize");
        var started = timeProvider.GetTimestamp();
        var ambiguousCommit = false;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(
                         IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false))
        {
            _ = await CentralArtifactRetentionLock.AcquireAsync(
                dbContext, snapshot.CentralArtifactId, cancellationToken).ConfigureAwait(false);
            _ = await CentralArtifactRetentionLock.AcquireDispositionAsync(
                dbContext, snapshot.DispositionId, cancellationToken).ConfigureAwait(false);

            var valid = await LoadFinalizationSnapshotQuery(snapshot)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var storageReference = _storageNames.ArtifactPrefix + snapshot.ObjectKey;
            if (valid is null
                || await references.IsHeldAsync(snapshot.CentralArtifactId, cancellationToken).ConfigureAwait(false)
                || await CentralObjectOwnershipFence.HasActiveOwnerAsync(
                    dbContext, storageReference, snapshot.CentralArtifactId, cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                await ScheduleConflictRetryAsync(snapshot).ConfigureAwait(false);
                telemetry.RecordFinalizationConflict(origin);
                telemetry.RecordStage("finalize", "conflict", origin, timeProvider.GetElapsedTime(started));
                LogFinalization("conflict", origin, "pending", snapshot.ByteLength,
                    timeProvider.GetElapsedTime(started).TotalMilliseconds);
                activity?.SetTag("retention.outcome", "conflict");
                return false;
            }

            var now = timeProvider.GetUtcNow();
            var artifactAffected = await dbContext.CentralArtifacts.Where(item =>
                    item.Id == snapshot.CentralArtifactId
                    && item.RetentionDeletionToken == snapshot.OperationToken
                    && item.ObjectState == CentralArtifactObjectState.Expired
                    && item.RetentionDeletionRequestedAtUtc == snapshot.ArtifactRequestedAtUtc
                    && item.RetentionDeletionCompletedAtUtc == null
                    && item.RowVersion == snapshot.ArtifactRowVersion
                    && EF.Functions.Collate(item.StorageReference, CentralObjectOwnershipFence.BinaryCollation)
                        == storageReference)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.RetentionDeletionCompletedAtUtc, now), cancellationToken)
                .ConfigureAwait(false);
            var dispositionAffected = await dbContext.CentralObjectRecoveryDispositions.Where(item =>
                    item.Id == snapshot.DispositionId
                    && item.CentralArtifactId == snapshot.CentralArtifactId
                    && item.OperationToken == snapshot.OperationToken
                    && item.State == CentralObjectRecoveryStates.PendingDelete
                    && item.CompletedAtUtc == null
                    && item.AttemptCount == snapshot.AttemptCount
                    && item.LastAttemptAtUtc == snapshot.LastAttemptAtUtc
                    && item.RowVersion == snapshot.DispositionRowVersion
                    && EF.Functions.Collate(item.SourceObjectKey, CentralObjectOwnershipFence.BinaryCollation)
                        == snapshot.ObjectKey)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.State, CentralObjectRecoveryStates.Completed)
                    .SetProperty(item => item.CompletedAtUtc, now)
                    .SetProperty(item => item.NextAttemptAtUtc, (DateTimeOffset?)null)
                    .SetProperty(item => item.ReasonCode, (string?)null)
                    .SetProperty(item => item.UpdatedAtUtc, now), cancellationToken).ConfigureAwait(false);
            if (artifactAffected != 1 || dispositionAffected != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                telemetry.RecordFinalizationConflict(origin);
                return false;
            }
            try
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                IsCommitOutcomeAmbiguous(exception) && !IsSqlDeadlock(exception))
            {
                ambiguousCommit = true;
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception rollbackException) when (IsCommitOutcomeAmbiguous(rollbackException))
                {
                }
            }
        }
        dbContext.ChangeTracker.Clear();
        if (ambiguousCommit)
        {
            var completed = await ProbeCompletedAsync(snapshot).ConfigureAwait(false);
            telemetry.RecordStage("finalize", completed ? "deleted" : "conflict", origin,
                timeProvider.GetElapsedTime(started));
            activity?.SetTag("retention.outcome", completed ? "deleted" : "conflict");
            return completed;
        }
        var elapsed = timeProvider.GetElapsedTime(started);
        telemetry.RecordStage("finalize", "deleted", origin, elapsed);
        LogFinalization("deleted", origin, "completed", snapshot.ByteLength, elapsed.TotalMilliseconds);
        activity?.SetTag("retention.outcome", "deleted");
        return true;
    }

    private async Task PersistFailureAsync(RetentionSnapshot snapshot, string failureCategory, bool terminal)
    {
        var now = timeProvider.GetUtcNow();
        var nextAttempt = terminal ? (DateTimeOffset?)null : now + GetRetryDelay(snapshot.AttemptCount);
        await dbContext.CentralObjectRecoveryDispositions.Where(item =>
                item.Id == snapshot.DispositionId
                && item.OperationToken == snapshot.OperationToken
                && item.State == CentralObjectRecoveryStates.PendingDelete
                && item.RowVersion == snapshot.DispositionRowVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, terminal
                    ? CentralObjectRecoveryStates.Failed
                    : CentralObjectRecoveryStates.PendingDelete)
                .SetProperty(item => item.NextAttemptAtUtc, nextAttempt)
                .SetProperty(item => item.CompletedAtUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.ReasonCode, terminal ? $"retention.{failureCategory}" : null)
                .SetProperty(item => item.UpdatedAtUtc, now), CancellationToken.None).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
    }

    private async Task PersistInvalidLinkedStateAsync(Guid dispositionId, CancellationToken cancellationToken)
    {
        var current = await dbContext.CentralObjectRecoveryDispositions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == dispositionId, cancellationToken).ConfigureAwait(false);
        if (current is null
            || current.OperationToken is null
            || current.State != CentralObjectRecoveryStates.PendingDelete)
        {
            return;
        }
        var now = timeProvider.GetUtcNow();
        _ = await dbContext.CentralObjectRecoveryDispositions.Where(item =>
                item.Id == current.Id
                && item.OperationToken == current.OperationToken
                && item.State == CentralObjectRecoveryStates.PendingDelete
                && item.RowVersion == current.RowVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, CentralObjectRecoveryStates.Failed)
                .SetProperty(item => item.AttemptCount, item => item.AttemptCount + 1)
                .SetProperty(item => item.LastAttemptAtUtc, now)
                .SetProperty(item => item.NextAttemptAtUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.CompletedAtUtc, (DateTimeOffset?)null)
                .SetProperty(item => item.ReasonCode, "retention.state-conflict")
                .SetProperty(item => item.UpdatedAtUtc, now), cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
    }

    private async Task ScheduleConflictRetryAsync(RetentionSnapshot snapshot)
    {
        var now = timeProvider.GetUtcNow();
        await dbContext.CentralObjectRecoveryDispositions.Where(item =>
                item.Id == snapshot.DispositionId
                && item.OperationToken == snapshot.OperationToken
                && item.State == CentralObjectRecoveryStates.PendingDelete
                && item.RowVersion == snapshot.DispositionRowVersion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.NextAttemptAtUtc, now + TimeSpan.FromSeconds(1))
                .SetProperty(item => item.UpdatedAtUtc, now), CancellationToken.None).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
    }

    private Task<RetentionSnapshot?> LoadSnapshotAsync(Guid dispositionId, CancellationToken cancellationToken)
        => LoadSnapshotQuery(dispositionId).SingleOrDefaultAsync(cancellationToken);

    private IQueryable<RetentionSnapshot> LoadSnapshotQuery(Guid dispositionId)
        => from disposition in dbContext.CentralObjectRecoveryDispositions.AsNoTracking()
           join artifact in dbContext.CentralArtifacts.AsNoTracking()
               on disposition.CentralArtifactId equals (Guid?)artifact.Id
           where disposition.Id == dispositionId
               && disposition.Kind == CentralObjectRecoveryKinds.ExpiredDelete
               && disposition.OperationToken != null
               && disposition.CentralArtifactId != null
               && artifact.RetentionDeletionToken == disposition.OperationToken
               && EF.Functions.Collate(artifact.StorageReference, CentralObjectOwnershipFence.BinaryCollation)
                   == _storageNames.ArtifactPrefix + disposition.SourceObjectKey
           select new RetentionSnapshot(
               disposition.Id,
               disposition.CentralArtifactId!.Value,
               disposition.OperationToken!.Value,
               disposition.SourceObjectKey,
               disposition.State,
               artifact.ObjectState,
               disposition.ByteLength,
                disposition.AttemptCount,
                disposition.NextAttemptAtUtc,
                disposition.LastAttemptAtUtc,
                artifact.RetentionDeletionRequestedAtUtc,
               artifact.RetentionDeletionCompletedAtUtc,
               disposition.CompletedAtUtc,
               disposition.RowVersion,
               artifact.RowVersion);

    private IQueryable<RetentionSnapshot> LoadFinalizationSnapshotQuery(RetentionSnapshot expected)
        => from disposition in dbContext.CentralObjectRecoveryDispositions.AsNoTracking()
           join artifact in dbContext.CentralArtifacts.AsNoTracking()
               on disposition.CentralArtifactId equals (Guid?)artifact.Id
           where disposition.Id == expected.DispositionId
               && disposition.Kind == CentralObjectRecoveryKinds.ExpiredDelete
               && disposition.CentralArtifactId == expected.CentralArtifactId
               && disposition.OperationToken == expected.OperationToken
               && disposition.State == CentralObjectRecoveryStates.PendingDelete
               && disposition.CompletedAtUtc == null
               && disposition.AttemptCount == expected.AttemptCount
               && disposition.LastAttemptAtUtc == expected.LastAttemptAtUtc
               && disposition.RowVersion == expected.DispositionRowVersion
               && EF.Functions.Collate(disposition.SourceObjectKey, CentralObjectOwnershipFence.BinaryCollation)
                   == expected.ObjectKey
               && artifact.RetentionDeletionToken == expected.OperationToken
               && artifact.ObjectState == CentralArtifactObjectState.Expired
               && artifact.RetentionDeletionRequestedAtUtc == expected.ArtifactRequestedAtUtc
               && artifact.RetentionDeletionCompletedAtUtc == null
               && artifact.RowVersion == expected.ArtifactRowVersion
               && EF.Functions.Collate(artifact.StorageReference, CentralObjectOwnershipFence.BinaryCollation)
                   == _storageNames.ArtifactPrefix + expected.ObjectKey
           select new RetentionSnapshot(
               disposition.Id,
               disposition.CentralArtifactId!.Value,
               disposition.OperationToken!.Value,
               disposition.SourceObjectKey,
               disposition.State,
               artifact.ObjectState,
               disposition.ByteLength,
               disposition.AttemptCount,
               disposition.NextAttemptAtUtc,
               disposition.LastAttemptAtUtc,
               artifact.RetentionDeletionRequestedAtUtc,
               artifact.RetentionDeletionCompletedAtUtc,
               disposition.CompletedAtUtc,
               disposition.RowVersion,
               artifact.RowVersion);

    private async Task<bool> ProbeCompletedAsync(RetentionSnapshot expected)
    {
        await using var probe = CreateProbeContext();
        var current = await (from disposition in probe.CentralObjectRecoveryDispositions.AsNoTracking()
                             join artifact in probe.CentralArtifacts.AsNoTracking()
                                 on disposition.CentralArtifactId equals (Guid?)artifact.Id
                             where disposition.Id == expected.DispositionId
                                 && disposition.CentralArtifactId == expected.CentralArtifactId
                                 && disposition.OperationToken == expected.OperationToken
                                 && disposition.State == CentralObjectRecoveryStates.Completed
                                 && disposition.CompletedAtUtc != null
                                 && artifact.RetentionDeletionToken == expected.OperationToken
                                 && artifact.ObjectState == CentralArtifactObjectState.Expired
                                 && artifact.RetentionDeletionCompletedAtUtc != null
                                 && EF.Functions.Collate(
                                     disposition.SourceObjectKey,
                                     CentralObjectOwnershipFence.BinaryCollation) == expected.ObjectKey
                                 && EF.Functions.Collate(
                                     artifact.StorageReference,
                                     CentralObjectOwnershipFence.BinaryCollation)
                                     == _storageNames.ArtifactPrefix + expected.ObjectKey
                             select new
                             {
                                 artifact.RetentionDeletionRequestedAtUtc,
                                 artifact.RetentionDeletionCompletedAtUtc,
                                 disposition.CompletedAtUtc
                             }).SingleOrDefaultAsync(CancellationToken.None).ConfigureAwait(false);
        return current is not null
            && current.RetentionDeletionRequestedAtUtc.HasValue
            && current.RetentionDeletionCompletedAtUtc >= current.RetentionDeletionRequestedAtUtc
            && current.CompletedAtUtc >= current.RetentionDeletionRequestedAtUtc;
    }

    private ApplicationDbContext CreateProbeContext()
    {
        var connectionString = dbContext.Database.GetConnectionString()
            ?? throw new InvalidOperationException("Retention recovery requires SQL Server.");
        return new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(connectionString).Options);
    }

    private static bool IsDurablyCompleted(RetentionSnapshot snapshot)
        => snapshot.ArtifactState == CentralArtifactObjectState.Expired
            && snapshot.ArtifactRequestedAtUtc.HasValue
            && snapshot.ArtifactCompletedAtUtc.HasValue
            && snapshot.DispositionCompletedAtUtc.HasValue
            && snapshot.ArtifactCompletedAtUtc >= snapshot.ArtifactRequestedAtUtc
            && snapshot.DispositionCompletedAtUtc >= snapshot.ArtifactRequestedAtUtc;

    private static bool IsCommitOutcomeAmbiguous(Exception exception)
        => exception is OperationCanceledException or DbException or InvalidOperationException;

    private bool IsSqlDeadlock(Exception exception)
        => CentralArtifactRetentionService.IsSqlServerDeadlock(exception)
            || SqlDeadlockClassifier?.Invoke(exception) == true;

    private static TimeSpan GetRetryDelay(int attemptCount)
        => TimeSpan.FromSeconds(Math.Min(
            MaximumRetryDelay.TotalSeconds,
            Math.Pow(2, Math.Clamp(attemptCount - 1, 0, 20))));

    private static bool IsObjectStoreFailure(Exception exception)
        => exception is ObjectStoreException
            or OperationCanceledException
            or ArgumentException
            or NotSupportedException
            or InvalidOperationException;

    private static bool IsTerminal(Exception exception)
    {
        if (exception is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return true;
        }
        return exception is ObjectStoreException objectStoreException && objectStoreException.IsTerminal;
    }

    private static string GetFailureCategory(Exception exception)
    {
        if (exception is OperationCanceledException) return "timeout";
        if (exception is ArgumentException) return "invalid-reference";
        if (exception is NotSupportedException) return "unsupported-storage";
        if (exception is InvalidOperationException) return "configuration";
        if (exception is ObjectStoreException objectStoreException)
        {
            return objectStoreException.Kind switch
            {
                ObjectStoreFailureKind.Authentication => "authentication",
                ObjectStoreFailureKind.Authorization => "authorization",
                ObjectStoreFailureKind.MissingBucket => "configuration",
                ObjectStoreFailureKind.Unsupported => "unsupported-storage",
                ObjectStoreFailureKind.Timeout or ObjectStoreFailureKind.Canceled => "timeout",
                ObjectStoreFailureKind.Transient or ObjectStoreFailureKind.Throttled => "connection",
                _ => "object-store"
            };
        }
        return "object-store";
    }

    [LoggerMessage(2171, LogLevel.Information,
        "Central artifact retention delete: Outcome={Outcome} Origin={Origin} FailureCategory={FailureCategory} Bytes={Bytes} DurationMs={DurationMs}")]
    private partial void LogDelete(string outcome, string origin, string failureCategory, long bytes, double durationMs);

    [LoggerMessage(2172, LogLevel.Information,
        "Central artifact retention finalization: Outcome={Outcome} Origin={Origin} State={State} Bytes={Bytes} DurationMs={DurationMs}")]
    private partial void LogFinalization(string outcome, string origin, string state, long bytes, double durationMs);

    [LoggerMessage(2174, LogLevel.Error,
        "Central artifact retention terminal failure: State={State} Origin={Origin} FailureCategory={FailureCategory} Bytes={Bytes}")]
    private partial void LogTerminalFailure(string state, string origin, string failureCategory, long bytes);

    private sealed record RetentionSnapshot(
        Guid DispositionId,
        Guid CentralArtifactId,
        Guid OperationToken,
        string ObjectKey,
        string State,
        CentralArtifactObjectState ArtifactState,
        long ByteLength,
        int AttemptCount,
        DateTimeOffset? NextAttemptAtUtc,
        DateTimeOffset? LastAttemptAtUtc,
        DateTimeOffset? ArtifactRequestedAtUtc,
        DateTimeOffset? ArtifactCompletedAtUtc,
        DateTimeOffset? DispositionCompletedAtUtc,
        byte[] DispositionRowVersion,
        byte[] ArtifactRowVersion);
}
