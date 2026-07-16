using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralDerivativeJobService
{
    Task<CentralDerivativeJobLease?> ClaimNextAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task<CentralDerivativeJobLease> RenewLeaseAsync(Guid jobId, Guid leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task CompleteAsync(Guid jobId, Guid leaseToken, Guid resultArtifactId, CancellationToken cancellationToken);

    Task FailAsync(Guid jobId, Guid leaseToken, string error, bool retryable, CancellationToken cancellationToken);

    Task SkipAsync(Guid jobId, Guid leaseToken, string reasonCode, CancellationToken cancellationToken);

    Task MarkInputUnavailableAsync(
        Guid jobId,
        Guid leaseToken,
        byte[] expectedSourceRowVersion,
        string reasonCode,
        bool quarantine,
        CancellationToken cancellationToken);
}

internal sealed class CentralDerivativeJobService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : ICentralDerivativeJobService
{
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumLeaseDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromHours(1);

    public async Task<CentralDerivativeJobLease?> ClaimNextAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (workerId.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }
        ValidateLeaseDuration(leaseDuration);
        for (var deadlockRetry = 0; deadlockRetry < 100; deadlockRetry++)
        {
            try
            {
                return await ClaimNextCoreAsync(workerId, leaseDuration, cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException exception) when (exception.Number == 1205)
            {
                dbContext.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50, deadlockRetry + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (DbUpdateException exception) when (exception.InnerException is SqlException { Number: 1205 })
            {
                dbContext.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50, deadlockRetry + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (exception.GetBaseException() is SqlException { Number: 1205 })
            {
                dbContext.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(50, deadlockRetry + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        throw new CentralDerivativeJobStateException(
            "Unable to claim a derivative job because SQL deadlocks persisted after bounded retry.");
    }

    private async Task<CentralDerivativeJobLease?> ClaimNextCoreAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        for (var collision = 0; collision < 100; collision++)
        {
            var now = timeProvider.GetUtcNow();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var candidate = await dbContext.CentralDerivativeJobs
                .FromSqlInterpolated($"""
                    SELECT TOP(1) job.*
                    FROM [CentralDerivativeJobs] AS job WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK, ROWLOCK)
                    WHERE
                        ((job.[Status] IN (N'Pending', N'RetryableFailure')
                                AND job.[AttemptCount] < job.[MaxAttempts]
                                AND job.[AvailableAtUtc] <= {now})
                            OR (job.[Status] = N'Leased'
                                AND job.[LeaseExpiresAtUtc] <= {now}
                                AND job.[AttemptCount] < job.[MaxAttempts]))
                            AND EXISTS (
                                SELECT 1
                                FROM [CentralArtifacts] AS source
                                WHERE source.[Id] = job.[SourceCentralArtifactId]
                                    AND source.[ObjectState] = N'Available'
                                    AND source.[ReconstructionState] = N'Complete')
                        OR (job.[Status] = N'Leased'
                            AND job.[LeaseExpiresAtUtc] <= {now}
                            AND job.[AttemptCount] >= job.[MaxAttempts])
                    ORDER BY
                        CASE WHEN job.[Status] = N'Leased' THEN job.[LeaseExpiresAtUtc] ELSE job.[AvailableAtUtc] END,
                        job.[CreatedAtUtc],
                        job.[Id]
                    """)
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (candidate is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }

            var leaseToken = Guid.NewGuid();
            var leaseExpiresAtUtc = now + leaseDuration;
            var attemptNumber = candidate.AttemptCount + 1;
            if (candidate.Status == CentralDerivativeJobStatus.Leased)
            {
                var expiredAttempt = await dbContext.CentralDerivativeJobAttempts.Where(attempt =>
                        attempt.CentralDerivativeJobId == candidate.Id
                        && attempt.AttemptNumber == candidate.AttemptCount
                        && attempt.Outcome == CentralDerivativeAttemptOutcome.Leased
                        && attempt.LeaseExpiresAtUtc <= now)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(attempt => attempt.Outcome, CentralDerivativeAttemptOutcome.LeaseExpired)
                        .SetProperty(attempt => attempt.EndedAtUtc, now)
                        .SetProperty(attempt => attempt.ReasonCode, "lease.expired"), cancellationToken)
                    .ConfigureAwait(false);
                if (expiredAttempt != 1)
                {
                    var inconsistent = await dbContext.CentralDerivativeJobs.Where(job =>
                            job.Id == candidate.Id
                            && job.Status == CentralDerivativeJobStatus.Leased
                            && job.AttemptCount == candidate.AttemptCount
                            && job.LeaseToken == candidate.LeaseToken
                            && job.LeaseExpiresAtUtc <= now)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                            .SetProperty(job => job.LastFailedAtUtc, now)
                            .SetProperty(job => job.LastError, "The expired derivative lease has no active attempt record.")
                            .SetProperty(job => job.UpdatedAtUtc, now)
                            .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                            .SetProperty(job => job.LeaseOwner, (string?)null)
                            .SetProperty(job => job.LeaseToken, (Guid?)null)
                            .SetProperty(job => job.LeaseAcquiredAtUtc, (DateTimeOffset?)null)
                            .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null), cancellationToken)
                        .ConfigureAwait(false);
                    if (inconsistent == 1)
                    {
                        await QuarantineAbandonedOutputAsync(candidate.Id, now, cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        dbContext.ChangeTracker.Clear();
                        collision--;
                        continue;
                    }
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    dbContext.ChangeTracker.Clear();
                    continue;
                }
                if (candidate.AttemptCount >= candidate.MaxAttempts)
                {
                    var terminal = await dbContext.CentralDerivativeJobs.Where(job =>
                            job.Id == candidate.Id
                            && job.Status == CentralDerivativeJobStatus.Leased
                            && job.AttemptCount == candidate.AttemptCount
                            && job.LeaseToken == candidate.LeaseToken
                            && job.LeaseExpiresAtUtc <= now)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                            .SetProperty(job => job.LastFailedAtUtc, now)
                            .SetProperty(job => job.LastError,
                                "The derivative job lease expired after the maximum number of attempts.")
                            .SetProperty(job => job.UpdatedAtUtc, now)
                            .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                            .SetProperty(job => job.LeaseOwner, (string?)null)
                            .SetProperty(job => job.LeaseToken, (Guid?)null)
                            .SetProperty(job => job.LeaseAcquiredAtUtc, (DateTimeOffset?)null)
                            .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null), cancellationToken)
                        .ConfigureAwait(false);
                    if (terminal != 1)
                    {
                        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                        dbContext.ChangeTracker.Clear();
                        continue;
                    }
                    await QuarantineAbandonedOutputAsync(candidate.Id, now, cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    dbContext.ChangeTracker.Clear();
                    collision--;
                    continue;
                }
            }
            var affected = await dbContext.CentralDerivativeJobs.Where(job =>
                    job.Id == candidate.Id
                    && job.AttemptCount == candidate.AttemptCount
                    && job.AttemptCount < job.MaxAttempts
                    && job.SourceArtifact!.ObjectState == CentralArtifactObjectState.Available
                    && job.SourceArtifact.ReconstructionState == CentralReconstructionState.Complete
                    && ((job.Status == CentralDerivativeJobStatus.Pending
                            || job.Status == CentralDerivativeJobStatus.RetryableFailure)
                        && job.AvailableAtUtc <= now
                        || job.Status == CentralDerivativeJobStatus.Leased && job.LeaseExpiresAtUtc <= now))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, CentralDerivativeJobStatus.Leased)
                    .SetProperty(job => job.AttemptCount, attemptNumber)
                    .SetProperty(job => job.LeaseOwner, workerId)
                    .SetProperty(job => job.LeaseToken, leaseToken)
                    .SetProperty(job => job.LeaseAcquiredAtUtc, now)
                    .SetProperty(job => job.LeaseExpiresAtUtc, leaseExpiresAtUtc)
                    .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                    .SetProperty(job => job.UpdatedAtUtc, now), cancellationToken)
                .ConfigureAwait(false);
            if (affected != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                continue;
            }
            dbContext.CentralDerivativeJobAttempts.Add(new CentralDerivativeJobAttempt
            {
                CentralDerivativeJobId = candidate.Id,
                AttemptNumber = attemptNumber,
                WorkerId = workerId,
                LeaseAcquiredAtUtc = now,
                LeaseExpiresAtUtc = leaseExpiresAtUtc,
                Outcome = CentralDerivativeAttemptOutcome.Leased
            });
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var claimed = await LoadJobAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return CreateLease(claimed);
        }
        throw new CentralDerivativeJobStateException("Unable to claim a derivative job because of sustained concurrency.");
    }

    public async Task<CentralDerivativeJobLease> RenewLeaseAsync(
        Guid jobId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ValidateLeaseDuration(leaseDuration);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var attemptNumber = await dbContext.CentralDerivativeJobs.Where(job =>
                job.Id == jobId
                && job.Status == CentralDerivativeJobStatus.Leased
                && job.LeaseToken == leaseToken
                && job.LeaseExpiresAtUtc > now)
            .Select(job => (int?)job.AttemptCount)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (!attemptNumber.HasValue)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
        }
        var affected = await dbContext.CentralDerivativeJobs.Where(job =>
                job.Id == jobId
                && job.Status == CentralDerivativeJobStatus.Leased
                && job.LeaseToken == leaseToken
                && job.LeaseExpiresAtUtc > now
                && job.SourceArtifact!.ObjectState == CentralArtifactObjectState.Available
                && job.SourceArtifact.ReconstructionState == CentralReconstructionState.Complete)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.LeaseExpiresAtUtc, now + leaseDuration)
                .SetProperty(job => job.UpdatedAtUtc, now), cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
        }
        var attemptAffected = await dbContext.CentralDerivativeJobAttempts.Where(attempt =>
                attempt.CentralDerivativeJobId == jobId
                && attempt.AttemptNumber == attemptNumber.Value
                && attempt.Outcome == CentralDerivativeAttemptOutcome.Leased)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(attempt => attempt.LeaseExpiresAtUtc, now + leaseDuration), cancellationToken)
            .ConfigureAwait(false);
        if (attemptAffected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("The derivative job attempt is missing or invalid.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        return CreateLease(await LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false));
    }

    public async Task CompleteAsync(
        Guid jobId,
        Guid leaseToken,
        Guid resultArtifactId,
        CancellationToken cancellationToken)
    {
        var job = await LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job.Status == CentralDerivativeJobStatus.Completed)
        {
            if (job.ResultArtifact?.ArtifactId == resultArtifactId
                && IsUsable(job.SourceArtifact)
                && IsUsable(job.ResultArtifact))
            {
                return;
            }
            throw new CentralDerivativeJobStateException("The derivative job is already completed with a different artifact.");
        }

        var result = await dbContext.CentralArtifacts.SingleOrDefaultAsync(
            artifact => artifact.ArtifactId == resultArtifactId
                && artifact.CentralFrameId == job.SourceArtifact!.CentralFrameId,
            cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative result artifact does not exist.");
        if (result.CentralFrameId != job.SourceArtifact!.CentralFrameId
            || result.Role != job.TargetRole
            || result.RecipeVersion != job.TargetRecipeVersion
            || (result.Variant ?? string.Empty) != job.TargetVariant)
        {
            throw new CentralDerivativeJobStateException("The derivative result does not satisfy the job target identity.");
        }
        if (!IsUsable(job.SourceArtifact) || !IsUsable(result))
        {
            throw new CentralDerivativeJobStateException("The derivative source and result artifacts must be available and completely reconstructed.");
        }

        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var affected = await dbContext.CentralDerivativeJobs.Where(candidate =>
                candidate.Id == jobId
                && candidate.Status == CentralDerivativeJobStatus.Leased
                && candidate.LeaseToken == leaseToken
                && candidate.LeaseExpiresAtUtc > now
                && candidate.SourceArtifact!.ObjectState == CentralArtifactObjectState.Available
                && candidate.SourceArtifact.ReconstructionState == CentralReconstructionState.Complete
                && dbContext.CentralArtifacts.Any(artifact => artifact.Id == result.Id
                    && artifact.ObjectState == CentralArtifactObjectState.Available
                    && artifact.ReconstructionState == CentralReconstructionState.Complete))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, CentralDerivativeJobStatus.Completed)
                .SetProperty(candidate => candidate.ResultCentralArtifactId, result.Id)
                .SetProperty(candidate => candidate.CompletedAtUtc, now)
                .SetProperty(candidate => candidate.UpdatedAtUtc, now)
                .SetProperty(candidate => candidate.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.LeaseOwner, (string?)null)
                .SetProperty(candidate => candidate.LeaseToken, (Guid?)null)
                .SetProperty(candidate => candidate.LeaseAcquiredAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.LeaseExpiresAtUtc, (DateTimeOffset?)null),
                cancellationToken).ConfigureAwait(false);
        if (affected == 1)
        {
            await CompleteAttemptAsync(
                jobId, job.AttemptCount, CentralDerivativeAttemptOutcome.Completed, null, now, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            dbContext.ChangeTracker.Clear();
            return;
        }
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        var completed = await dbContext.CentralDerivativeJobs.Include(candidate => candidate.ResultArtifact)
            .SingleOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken).ConfigureAwait(false);
        if (completed?.Status == CentralDerivativeJobStatus.Completed
            && completed.ResultArtifact?.ArtifactId == resultArtifactId)
        {
            return;
        }
        throw new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
    }

    public async Task FailAsync(
        Guid jobId,
        Guid leaseToken,
        string error,
        bool retryable,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        var job = await LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var terminal = !retryable || job.AttemptCount >= job.MaxAttempts;
        var nextStatus = terminal ? CentralDerivativeJobStatus.TerminalFailure : CentralDerivativeJobStatus.RetryableFailure;
        var availableAtUtc = terminal
            ? (DateTimeOffset?)null
            : now + CalculateRetryDelay(job.AttemptCount, InitialRetryDelay, MaximumRetryDelay);
        var lastError = error.Length <= 2048 ? error : error[..2048];
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var affected = await dbContext.CentralDerivativeJobs.Where(candidate =>
                candidate.Id == jobId
                && candidate.Status == CentralDerivativeJobStatus.Leased
                && candidate.LeaseToken == leaseToken
                && candidate.LeaseExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, nextStatus)
                .SetProperty(candidate => candidate.AvailableAtUtc, availableAtUtc)
                .SetProperty(candidate => candidate.LastFailedAtUtc, now)
                .SetProperty(candidate => candidate.LastError, lastError)
                .SetProperty(candidate => candidate.UpdatedAtUtc, now)
                .SetProperty(candidate => candidate.LeaseOwner, (string?)null)
                .SetProperty(candidate => candidate.LeaseToken, (Guid?)null)
                .SetProperty(candidate => candidate.LeaseAcquiredAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.LeaseExpiresAtUtc, (DateTimeOffset?)null),
                cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
        }
        if (terminal)
        {
            await QuarantineAbandonedOutputAsync(jobId, now, cancellationToken).ConfigureAwait(false);
        }
        await CompleteAttemptAsync(
            jobId,
            job.AttemptCount,
            terminal ? CentralDerivativeAttemptOutcome.TerminalFailure : CentralDerivativeAttemptOutcome.RetryableFailure,
            retryable ? "processing.retryable-failure" : "processing.terminal-failure",
            now,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
    }

    public async Task SkipAsync(
        Guid jobId,
        Guid leaseToken,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        var job = await LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var boundedReason = reasonCode.Length <= 256 ? reasonCode : reasonCode[..256];
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var affected = await dbContext.CentralDerivativeJobs.Where(candidate =>
                candidate.Id == jobId
                && candidate.Status == CentralDerivativeJobStatus.Leased
                && candidate.LeaseToken == leaseToken
                && candidate.LeaseExpiresAtUtc > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, CentralDerivativeJobStatus.Skipped)
                .SetProperty(candidate => candidate.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.LastError, boundedReason)
                .SetProperty(candidate => candidate.UpdatedAtUtc, now)
                .SetProperty(candidate => candidate.LeaseOwner, (string?)null)
                .SetProperty(candidate => candidate.LeaseToken, (Guid?)null)
                .SetProperty(candidate => candidate.LeaseAcquiredAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.LeaseExpiresAtUtc, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
        if (affected != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
        }
        await CompleteAttemptAsync(
            jobId, job.AttemptCount, CentralDerivativeAttemptOutcome.Skipped, boundedReason, now, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
    }

    public async Task MarkInputUnavailableAsync(
        Guid jobId,
        Guid leaseToken,
        byte[] expectedSourceRowVersion,
        string reasonCode,
        bool quarantine,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentNullException.ThrowIfNull(expectedSourceRowVersion);
        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var job = await dbContext.CentralDerivativeJobs
            .Include(candidate => candidate.SourceArtifact)
            .SingleOrDefaultAsync(candidate => candidate.Id == jobId
                && candidate.Status == CentralDerivativeJobStatus.Leased
                && candidate.LeaseToken == leaseToken
                && candidate.LeaseExpiresAtUtc > now,
                cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
        var source = job.SourceArtifact!;
        if (!source.RowVersion.AsSpan().SequenceEqual(expectedSourceRowVersion))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException(
                "The derivative source changed after object verification.");
        }
        source.ObjectState = quarantine
            ? CentralArtifactObjectState.Quarantined
            : CentralArtifactObjectState.Pending;
        source.ReconstructionState = quarantine
            ? CentralReconstructionState.Quarantined
            : CentralReconstructionState.PendingReference;
        source.StateReasonCode = reasonCode;
        source.ReconciledAtUtc = null;
        await ArtifactIngestService.InvalidateDependentsAsync(dbContext, source, cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var activeStatuses = new[]
        {
            CentralDerivativeJobStatus.Pending,
            CentralDerivativeJobStatus.Leased,
            CentralDerivativeJobStatus.RetryableFailure,
            CentralDerivativeJobStatus.Completed
        };
        var affectedJobIds = await dbContext.CentralDerivativeJobs
            .Where(candidate => candidate.SourceCentralArtifactId == source.Id
                && activeStatuses.Contains(candidate.Status))
            .Select(candidate => candidate.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        await dbContext.CentralDerivativeJobs.Where(candidate => affectedJobIds.Contains(candidate.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, quarantine
                    ? CentralDerivativeJobStatus.Quarantined
                    : CentralDerivativeJobStatus.RetryableFailure)
                .SetProperty(candidate => candidate.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.LastFailedAtUtc, now)
                .SetProperty(candidate => candidate.LastError, quarantine
                    ? reasonCode
                    : CentralDerivativeJobScheduler.SourceInvalidatedReason)
                .SetProperty(candidate => candidate.UpdatedAtUtc, now)
                .SetProperty(candidate => candidate.LeaseOwner, (string?)null)
                .SetProperty(candidate => candidate.LeaseToken, (Guid?)null)
                .SetProperty(candidate => candidate.LeaseAcquiredAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.LeaseExpiresAtUtc, (DateTimeOffset?)null), cancellationToken)
            .ConfigureAwait(false);
        await dbContext.CentralDerivativeJobAttempts.Where(attempt =>
                affectedJobIds.Contains(attempt.CentralDerivativeJobId)
                && attempt.Outcome == CentralDerivativeAttemptOutcome.Leased)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(attempt => attempt.Outcome, quarantine
                    ? CentralDerivativeAttemptOutcome.Quarantined
                    : CentralDerivativeAttemptOutcome.RetryableFailure)
                .SetProperty(attempt => attempt.ReasonCode, reasonCode)
                .SetProperty(attempt => attempt.EndedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
    }

    internal static TimeSpan CalculateRetryDelay(int attempt, TimeSpan initialDelay, TimeSpan maximumDelay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(initialDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDelay, initialDelay);
        var multiplier = 1L << Math.Min(attempt - 1, 30);
        var ticks = initialDelay.Ticks > maximumDelay.Ticks / multiplier
            ? maximumDelay.Ticks
            : initialDelay.Ticks * multiplier;
        return TimeSpan.FromTicks(Math.Min(ticks, maximumDelay.Ticks));
    }

    private async Task<CentralDerivativeJob> LoadJobAsync(Guid jobId, CancellationToken cancellationToken)
        => await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Include(job => job.SourceArtifact)!.ThenInclude(artifact => artifact!.Frame)
            .Include(job => job.ResultArtifact)
            .SingleOrDefaultAsync(job => job.Id == jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative job does not exist.");

    private async Task CompleteAttemptAsync(
        Guid jobId,
        int attemptNumber,
        CentralDerivativeAttemptOutcome outcome,
        string? reasonCode,
        DateTimeOffset endedAtUtc,
        CancellationToken cancellationToken)
    {
        var affected = await dbContext.CentralDerivativeJobAttempts.Where(attempt =>
                attempt.CentralDerivativeJobId == jobId
                && attempt.AttemptNumber == attemptNumber
                && attempt.Outcome == CentralDerivativeAttemptOutcome.Leased)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(attempt => attempt.Outcome, outcome)
                .SetProperty(attempt => attempt.ReasonCode, reasonCode)
                .SetProperty(attempt => attempt.EndedAtUtc, endedAtUtc), cancellationToken)
            .ConfigureAwait(false);
        if (affected != 1)
        {
            throw new CentralDerivativeJobStateException("The derivative job attempt is missing or invalid.");
        }
    }

    private Task<int> QuarantineAbandonedOutputAsync(
        Guid jobId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => dbContext.CentralArtifacts.Where(artifact =>
                (artifact.ObjectState == CentralArtifactObjectState.Pending
                    || artifact.ObjectState == CentralArtifactObjectState.Available)
                && dbContext.CentralArtifactProcessingEvidence.Any(evidence =>
                    evidence.CentralArtifactId == artifact.Id
                    && evidence.CentralDerivativeJobId == jobId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(artifact => artifact.ObjectState, CentralArtifactObjectState.Quarantined)
                .SetProperty(artifact => artifact.ReconstructionState, CentralReconstructionState.Quarantined)
                .SetProperty(artifact => artifact.StateReasonCode, "derivative.output-abandoned")
                .SetProperty(artifact => artifact.ReconciledAtUtc, now), cancellationToken);

    private static void ValidateLeaseDuration(TimeSpan leaseDuration)
    {
        if (leaseDuration < MinimumLeaseDuration || leaseDuration > MaximumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }
    }

    private static CentralDerivativeJobLease CreateLease(CentralDerivativeJob job)
    {
        var source = job.SourceArtifact ?? throw new InvalidOperationException("The derivative source artifact was not loaded.");
        var frame = source.Frame ?? throw new InvalidOperationException("The derivative source frame was not loaded.");
        return new CentralDerivativeJobLease(
            job.Id, job.LeaseToken!.Value, job.LeaseOwner!, job.LeaseExpiresAtUtc!.Value,
            frame.DevicePublicId, source.ArtifactId, source.Role, source.RecipeVersion,
            $"/api/v1.0/devices/{frame.DevicePublicId:D}/artifacts/{source.ArtifactId:D}/content",
            source.ChecksumSha256, source.MediaType, frame.FrameId, frame.AgentId,
            frame.CapturedAtUtc, frame.RigProfileVersion, frame.SceneProvenanceJson,
            job.TargetRole, job.TargetRecipeVersion, job.TargetVariant,
            job.RecipeName, job.RecipeOptionsJson, job.InputSelectorJson,
            job.RequestedRecipeIdentitySha256, job.RequestIdentitySha256,
            job.TraceParent, job.TraceState,
            job.AttemptCount, job.MaxAttempts);
    }

    private static bool IsUsable(CentralArtifact? artifact)
        => artifact?.ObjectState == CentralArtifactObjectState.Available
            && artifact.ReconstructionState == CentralReconstructionState.Complete;
}

internal sealed record CentralDerivativeJobLease(
    Guid JobId,
    Guid LeaseToken,
    string WorkerId,
    DateTimeOffset LeaseExpiresAtUtc,
    Guid SourceDevicePublicId,
    Guid SourceArtifactId,
    FrameArtifactRole SourceRole,
    string SourceRecipeVersion,
    string SourceContentUri,
    string SourceChecksumSha256,
    string SourceMediaType,
    Guid FrameId,
    string AgentId,
    DateTimeOffset CapturedAtUtc,
    int? RigProfileVersion,
    string? SceneProvenanceJson,
    FrameArtifactRole TargetRole,
    string TargetRecipeVersion,
    string TargetVariant,
    string RecipeName,
    string RecipeOptionsJson,
    string InputSelectorJson,
    string RequestedRecipeIdentitySha256,
    string RequestIdentitySha256,
    string? TraceParent,
    string? TraceState,
    int AttemptCount,
    int MaxAttempts);

internal sealed class CentralDerivativeJobStateException : Exception
{
    public CentralDerivativeJobStateException()
    {
    }

    public CentralDerivativeJobStateException(string message) : base(message)
    {
    }

    public CentralDerivativeJobStateException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

internal static class CentralDerivativeJobLock
{
    public static Task<int> AcquireAsync(
        ApplicationDbContext dbContext,
        Guid jobId,
        CancellationToken cancellationToken)
        => dbContext.Database.SqlQuery<int>(
                $"SELECT CAST(1 AS int) AS [Value] FROM [CentralDerivativeJobs] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {jobId}")
            .SingleOrDefaultAsync(cancellationToken);
}
