using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal interface ICentralDerivativeJobService
{
    Task<CentralDerivativeJobLease?> ClaimNextAsync(string workerId, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task<CentralDerivativeJobLease> RenewLeaseAsync(Guid jobId, Guid leaseToken, TimeSpan leaseDuration, CancellationToken cancellationToken);

    Task CompleteAsync(Guid jobId, Guid leaseToken, Guid resultArtifactId, CancellationToken cancellationToken);

    Task CompleteWithoutArtifactAsync(
        Guid jobId,
        Guid leaseToken,
        string reasonCode,
        CancellationToken cancellationToken);

    Task FailAsync(Guid jobId, Guid leaseToken, string error, bool retryable, CancellationToken cancellationToken);

    Task SkipAsync(Guid jobId, Guid leaseToken, string reasonCode, CancellationToken cancellationToken);

    Task MarkInputUnavailableAsync(
        Guid jobId,
        Guid leaseToken,
        Guid centralArtifactId,
        byte[] expectedSourceRowVersion,
        string reasonCode,
        bool quarantine,
        CancellationToken cancellationToken);
}

/// <summary>Restricts a claim to (or away from) a recipe set; names must be built-in recipe names.</summary>
internal sealed record CentralDerivativeClaimScope(
    IReadOnlySet<string> Recipes,
    bool Include,
    long? MaximumInputBytes = null)
{
    public static CentralDerivativeClaimScope Only(IEnumerable<string> recipes, long? maximumInputBytes = null)
        => new(recipes.ToHashSet(StringComparer.Ordinal), true, maximumInputBytes);

    public static CentralDerivativeClaimScope Excluding(IEnumerable<string> recipes)
        => new(recipes.ToHashSet(StringComparer.Ordinal), false);
}

/// <summary>Lease operations used by the runner protocol; the in-process worker keeps <see cref="ICentralDerivativeJobService"/>.</summary>
internal interface ICentralDerivativeRunnerLeaseService
{
    Task<CentralDerivativeJobLease?> ClaimNextAsync(
        string workerId,
        TimeSpan leaseDuration,
        CentralDerivativeClaimScope? scope,
        CancellationToken cancellationToken);

    /// <summary>Loads the current lease when it is held by <paramref name="workerId"/> with <paramref name="leaseToken"/>.</summary>
    Task<CentralDerivativeJobLease> GetLeaseAsync(
        Guid jobId,
        Guid leaseToken,
        string workerId,
        CancellationToken cancellationToken);
}

internal sealed class CentralDerivativeJobService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    CentralDerivativeWorkerTelemetry? telemetry = null,
    CentralProcessingGraphConvergenceSignal? graphConvergenceSignal = null,
    IOptions<CentralProcessingRunnerOptions>? runnerOptions = null)
    : ICentralDerivativeJobService, ICentralDerivativeRunnerLeaseService
{
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumLeaseDuration = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromHours(1);

    /// <summary>
    /// The in-process claim. Recipes placed on runners by <see cref="CentralProcessingRunnerOptions"/> are excluded so
    /// a missing runner creates backlog instead of a silent in-process fallback.
    /// </summary>
    public Task<CentralDerivativeJobLease?> ClaimNextAsync(
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        var runnerPlaced = runnerOptions?.Value.ResolveRunnerPlacedRecipes();
        var scope = runnerPlaced is { Count: > 0 } ? CentralDerivativeClaimScope.Excluding(runnerPlaced) : null;
        return ClaimNextAsync(workerId, leaseDuration, scope, cancellationToken);
    }

    public async Task<CentralDerivativeJobLease?> ClaimNextAsync(
        string workerId,
        TimeSpan leaseDuration,
        CentralDerivativeClaimScope? scope,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (workerId.Length > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(workerId));
        }
        ValidateLeaseDuration(leaseDuration);
        if (scope is not null && (scope.Recipes.Count == 0
                || scope.Recipes.Any(static recipe => !BuiltInProcessingRecipes.TryGetDefinition(recipe, out _))))
        {
            throw new ArgumentException("A claim scope must name built-in recipes.", nameof(scope));
        }
        for (var deadlockRetry = 0; deadlockRetry < 100; deadlockRetry++)
        {
            try
            {
                return await ClaimNextCoreAsync(workerId, leaseDuration, scope, cancellationToken).ConfigureAwait(false);
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

    public async Task<CentralDerivativeJobLease> GetLeaseAsync(
        Guid jobId,
        Guid leaseToken,
        string workerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        CentralDerivativeJob job;
        try
        {
            job = await LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        }
        catch (CentralDerivativeJobStateException)
        {
            throw new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
        }
        // Validate the loaded row itself so a lease reclaimed between a check and the load can never be returned
        // under the old owner's token.
        var now = timeProvider.GetUtcNow();
        if (job.Status != CentralDerivativeJobStatus.Leased
            || job.LeaseToken != leaseToken
            || !string.Equals(job.LeaseOwner, workerId, StringComparison.Ordinal)
            || job.LeaseExpiresAtUtc is null
            || job.LeaseExpiresAtUtc <= now)
        {
            throw new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
        }
        return CreateLease(job);
    }

    private async Task<CentralDerivativeJobLease?> ClaimNextCoreAsync(
        string workerId,
        TimeSpan leaseDuration,
        CentralDerivativeClaimScope? scope,
        CancellationToken cancellationToken)
    {
        var includeRecipes = scope is { Include: true } ? string.Join(',', scope.Recipes.Order(StringComparer.Ordinal)) : string.Empty;
        var excludeRecipes = scope is { Include: false } ? string.Join(',', scope.Recipes.Order(StringComparer.Ordinal)) : string.Empty;
        var maximumInputBytes = scope?.MaximumInputBytes ?? long.MaxValue;
        for (var collision = 0; collision < 100; collision++)
        {
            var now = timeProvider.GetUtcNow();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var candidate = await dbContext.CentralDerivativeJobs
                .FromSqlInterpolated($"""
                    SELECT TOP(1) job.*
                    FROM [CentralDerivativeJobs] AS job WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK, ROWLOCK)
                    WHERE
                        ({includeRecipes} = N'' OR job.[RecipeName] IN (SELECT [value] FROM STRING_SPLIT({includeRecipes}, ',')))
                        AND ({excludeRecipes} = N'' OR job.[RecipeName] NOT IN (SELECT [value] FROM STRING_SPLIT({excludeRecipes}, ',')))
                        AND ((SELECT COALESCE(SUM(sized.[ByteLength]), 0)
                              FROM [CentralDerivativeJobInputs] AS sizedInput
                              INNER JOIN [CentralArtifacts] AS sized ON sized.[Id] = sizedInput.[CentralArtifactId]
                              WHERE sizedInput.[CentralDerivativeJobId] = job.[Id]) <= {maximumInputBytes})
                        AND ((job.[GraphExecutionId] IS NULL OR EXISTS (
                            SELECT 1
                            FROM [CentralProcessingGraphExecutions] AS execution
                            WHERE execution.[Id] = job.[GraphExecutionId]
                              AND execution.[ExpandedAtUtc] IS NOT NULL
                              AND execution.[Status] = N'Running'))
                        AND (((job.[Status] IN (N'Pending', N'RetryableFailure')
                                AND job.[AttemptCount] < job.[MaxAttempts]
                                AND job.[InputSetIdentitySha256] IS NOT NULL
                                AND job.[AvailableAtUtc] <= {now})
                            OR (job.[Status] = N'Leased'
                                AND job.[LeaseExpiresAtUtc] <= {now}
                                AND job.[AttemptCount] < job.[MaxAttempts]))
                            AND EXISTS (SELECT 1 FROM [CentralDerivativeJobInputs] AS input WHERE input.[CentralDerivativeJobId] = job.[Id])
                            AND NOT EXISTS (
                                SELECT 1
                                FROM [CentralDerivativeJobInputRequirements] AS requirement
                                WHERE requirement.[CentralDerivativeJobId] = job.[Id]
                                    AND requirement.[IsRequired] = CAST(1 AS bit)
                                    AND ((requirement.[ResolutionState] = N'Resolved'
                                            AND ((requirement.[SourceKind] = N'Artifact' AND NOT EXISTS (
                                                    SELECT 1 FROM [CentralDerivativeJobInputs] AS resolved
                                                    WHERE resolved.[CentralDerivativeJobId] = job.[Id]
                                                        AND resolved.[CentralDerivativeJobInputRequirementId] = requirement.[Id]))
                                                OR (requirement.[SourceKind] = N'EnvironmentalObservation' AND NOT EXISTS (
                                                    SELECT 1 FROM [CentralDerivativeJobCanonicalInputs] AS canonical
                                                    WHERE canonical.[CentralDerivativeJobId] = job.[Id]
                                                        AND canonical.[CentralDerivativeJobInputRequirementId] = requirement.[Id]))))
                                        OR (requirement.[ResolutionState] <> N'Resolved'
                                            AND NOT (requirement.[ResolutionState] = N'Missing'
                                                AND job.[MissingInputOutcome] = N'Run'))))
                            AND NOT EXISTS (
                                SELECT 1
                                FROM [CentralDerivativeJobInputs] AS input
                                INNER JOIN [CentralArtifacts] AS source ON source.[Id] = input.[CentralArtifactId]
                                WHERE input.[CentralDerivativeJobId] = job.[Id]
                                    AND (source.[ObjectState] <> N'Available'
                                        OR source.[ReconstructionState] <> N'Complete'))
                            OR (job.[Status] = N'Leased'
                            AND job.[LeaseExpiresAtUtc] <= {now}
                            AND job.[AttemptCount] >= job.[MaxAttempts])))
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
                var durableTransientOutcome = await dbContext.CentralTransientValidationJobs.AsNoTracking()
                    .Where(validation => validation.CentralDerivativeJobId == candidate.Id &&
                        validation.OutcomeRecordedAtUtc != null &&
                        !validation.IdentitySlots.Any(slot =>
                            slot.State == CentralTransientValidationIdentitySlotState.Reserved))
                    .Select(validation => new
                    {
                        validation.CommittedAtUtc,
                        validation.OutcomeReasonCode,
                        HasExtractionReceipt = validation.ExtractionReceipt != null
                    })
                    .SingleOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (durableTransientOutcome is not null &&
                    (!durableTransientOutcome.CommittedAtUtc.HasValue || durableTransientOutcome.HasExtractionReceipt))
                {
                    var adoptedReason = durableTransientOutcome.CommittedAtUtc.HasValue
                        ? CentralTransientRuntimeReasonCodes.OutputAdopted
                        : durableTransientOutcome.OutcomeReasonCode ?? CentralTransientRuntimeReasonCodes.OutputAdopted;
                    var adoptedAttempt = await dbContext.CentralDerivativeJobAttempts.Where(attempt =>
                            attempt.CentralDerivativeJobId == candidate.Id &&
                            attempt.AttemptNumber == candidate.AttemptCount &&
                            attempt.Outcome == CentralDerivativeAttemptOutcome.Leased)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(attempt => attempt.Outcome, CentralDerivativeAttemptOutcome.Completed)
                            .SetProperty(attempt => attempt.EndedAtUtc, now)
                            .SetProperty(attempt => attempt.ReasonCode, adoptedReason), cancellationToken)
                        .ConfigureAwait(false);
                    var adoptedJob = adoptedAttempt == 1
                        ? await dbContext.CentralDerivativeJobs.Where(job =>
                                job.Id == candidate.Id && job.Status == CentralDerivativeJobStatus.Leased &&
                                job.AttemptCount == candidate.AttemptCount && job.LeaseToken == candidate.LeaseToken &&
                                job.LeaseExpiresAtUtc <= now)
                            .ExecuteUpdateAsync(setters => setters
                                .SetProperty(job => job.Status, CentralDerivativeJobStatus.Completed)
                                .SetProperty(job => job.StateReasonCode, adoptedReason)
                                .SetProperty(job => job.CompletedAtUtc, now)
                                .SetProperty(job => job.UpdatedAtUtc, now)
                                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                                .SetProperty(job => job.LeaseOwner, (string?)null)
                                .SetProperty(job => job.LeaseToken, (Guid?)null)
                                .SetProperty(job => job.LeaseAcquiredAtUtc, (DateTimeOffset?)null)
                                .SetProperty(job => job.LeaseExpiresAtUtc, (DateTimeOffset?)null), cancellationToken)
                            .ConfigureAwait(false)
                        : 0;
                    if (adoptedJob == 1)
                    {
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        graphConvergenceSignal?.Signal(candidate.GraphExecutionId);
                        dbContext.ChangeTracker.Clear();
                        collision--;
                        continue;
                    }
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    dbContext.ChangeTracker.Clear();
                    continue;
                }
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
                        await FinalizeTransientSlotsAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
                        await CentralTransientValidationOutcome.RecordNeedsReviewAsync(
                            dbContext,
                            candidate.Id,
                            CentralTransientRuntimeReasonCodes.InconsistentCommittedOutput,
                            now,
                            cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        graphConvergenceSignal?.Signal(candidate.GraphExecutionId);
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
                    var selectedAtUtc = await dbContext.CentralDerivativeJobInputs.AsNoTracking()
                        .Where(input => input.CentralDerivativeJobId == candidate.Id)
                        .MinAsync(input => (DateTimeOffset?)input.SelectedAtUtc, cancellationToken)
                        .ConfigureAwait(false);
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
                    await FinalizeTransientSlotsAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
                    await CentralTransientValidationOutcome.RecordNeedsReviewAsync(
                        dbContext,
                        candidate.Id,
                        CentralTransientRuntimeReasonCodes.AttemptsExhausted,
                        now,
                        cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    graphConvergenceSignal?.Signal(candidate.GraphExecutionId);
                    if (selectedAtUtc.HasValue)
                    {
                        telemetry?.RecordWindowPinDuration(
                            candidate.RecipeName, now - selectedAtUtc.Value, "terminal");
                    }
                    dbContext.ChangeTracker.Clear();
                    collision--;
                    continue;
                }
            }
            var affected = await dbContext.CentralDerivativeJobs.Where(job =>
                    job.Id == candidate.Id
                    && job.AttemptCount == candidate.AttemptCount
                    && job.AttemptCount < job.MaxAttempts
                    && (job.GraphExecutionId == null || job.GraphExecution!.ExpandedAtUtc != null &&
                        job.GraphExecution.Status == CentralProcessingGraphExecutionStatus.Running)
                    && job.InputSetIdentitySha256 != null
                    && job.Inputs.Any()
                    && !job.InputRequirements.Any(requirement => requirement.IsRequired
                        && (requirement.ResolutionState == CentralDerivativeInputResolutionState.Resolved
                            ? requirement.SourceKind == CentralDerivativeInputSourceKind.Artifact
                                ? !job.Inputs.Any(input => input.CentralDerivativeJobInputRequirementId == requirement.Id)
                                : !job.CanonicalInputs.Any(input =>
                                    input.CentralDerivativeJobInputRequirementId == requirement.Id)
                            : requirement.ResolutionState != CentralDerivativeInputResolutionState.Missing
                                || job.MissingInputOutcome != CentralDerivativeWindowOutcome.Run))
                    && !job.Inputs.Any(input => input.Artifact!.ObjectState != CentralArtifactObjectState.Available
                        || input.Artifact.ReconstructionState != CentralReconstructionState.Complete)
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
        var leased = await ReadRenewalStateAsync(jobId, leaseToken, now, cancellationToken).ConfigureAwait(false);
        if (leased is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
        }
        if (leased.IsCanceled)
        {
            // Cancellation reached a leased node. Refusing renewal bounds the active lease: the worker cancels its
            // local execution and the lease reaches the expiry path where graph convergence records Canceled.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeLeaseCanceledException();
        }
        var attemptNumber = (int?)leased.AttemptCount;
        // The cancellation guard is part of the authoritative update predicate: a cancellation committed between the
        // read above and this statement must not extend the lease (the read only classifies the failure afterwards).
        var affected = await dbContext.CentralDerivativeJobs.Where(job =>
                job.Id == jobId
                && job.Status == CentralDerivativeJobStatus.Leased
                && job.LeaseToken == leaseToken
                && job.LeaseExpiresAtUtc > now
                && job.CancellationRequestedAtUtc == null
                && (job.GraphExecution == null
                    || job.GraphExecution.Status != CentralProcessingGraphExecutionStatus.CancelRequested
                        && job.GraphExecution.Status != CentralProcessingGraphExecutionStatus.Canceled)
                && job.InputSetIdentitySha256 != null
                && job.Inputs.Any()
                && !job.InputRequirements.Any(requirement => requirement.IsRequired
                    && (requirement.ResolutionState == CentralDerivativeInputResolutionState.Resolved
                        ? requirement.SourceKind == CentralDerivativeInputSourceKind.Artifact
                            ? !job.Inputs.Any(input => input.CentralDerivativeJobInputRequirementId == requirement.Id)
                            : !job.CanonicalInputs.Any(input =>
                                input.CentralDerivativeJobInputRequirementId == requirement.Id)
                        : requirement.ResolutionState != CentralDerivativeInputResolutionState.Missing
                            || job.MissingInputOutcome != CentralDerivativeWindowOutcome.Run))
                && !job.Inputs.Any(input => input.Artifact!.ObjectState != CentralArtifactObjectState.Available
                    || input.Artifact.ReconstructionState != CentralReconstructionState.Complete))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.LeaseExpiresAtUtc, now + leaseDuration)
                .SetProperty(job => job.UpdatedAtUtc, now), cancellationToken).ConfigureAwait(false);
        if (affected != 1)
        {
            var raced = await ReadRenewalStateAsync(jobId, leaseToken, now, cancellationToken).ConfigureAwait(false);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw raced is { IsCanceled: true }
                ? new CentralDerivativeLeaseCanceledException()
                : new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
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

    private sealed record RenewalState(int AttemptCount, bool IsCanceled);

    private async Task<RenewalState?> ReadRenewalStateAsync(
        Guid jobId,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => await dbContext.CentralDerivativeJobs.Where(job =>
                job.Id == jobId
                && job.Status == CentralDerivativeJobStatus.Leased
                && job.LeaseToken == leaseToken
                && job.LeaseExpiresAtUtc > now)
            .Select(job => new RenewalState(
                job.AttemptCount,
                job.CancellationRequestedAtUtc != null
                || job.GraphExecution != null
                    && (job.GraphExecution.Status == CentralProcessingGraphExecutionStatus.CancelRequested
                        || job.GraphExecution.Status == CentralProcessingGraphExecutionStatus.Canceled)))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

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
            graphConvergenceSignal?.Signal(job.GraphExecutionId);
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

    public async Task CompleteWithoutArtifactAsync(
        Guid jobId,
        Guid leaseToken,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        var job = await LoadJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job.Status == CentralDerivativeJobStatus.Completed && job.ResultCentralArtifactId is null)
        {
            return;
        }
        var boundedReason = reasonCode.Length <= 256 ? reasonCode : reasonCode[..256];
        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var affected = await dbContext.CentralDerivativeJobs.Where(candidate =>
                candidate.Id == jobId
                && candidate.Status == CentralDerivativeJobStatus.Leased
                && candidate.LeaseToken == leaseToken
                && candidate.LeaseExpiresAtUtc > now
                && candidate.ResultCentralArtifactId == null
                && candidate.Inputs.Any()
                && !candidate.Inputs.Any(input => input.Artifact!.ObjectState != CentralArtifactObjectState.Available
                    || input.Artifact.ReconstructionState != CentralReconstructionState.Complete))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, CentralDerivativeJobStatus.Completed)
                .SetProperty(candidate => candidate.StateReasonCode, boundedReason)
                .SetProperty(candidate => candidate.CompletedAtUtc, now)
                .SetProperty(candidate => candidate.UpdatedAtUtc, now)
                .SetProperty(candidate => candidate.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.LastError, (string?)null)
                .SetProperty(candidate => candidate.LeaseOwner, (string?)null)
                .SetProperty(candidate => candidate.LeaseToken, (Guid?)null)
                .SetProperty(candidate => candidate.LeaseAcquiredAtUtc, (DateTimeOffset?)null)
                .SetProperty(candidate => candidate.LeaseExpiresAtUtc, (DateTimeOffset?)null),
                cancellationToken).ConfigureAwait(false);
        if (affected == 1)
        {
            await CompleteAttemptAsync(
                jobId, job.AttemptCount, CentralDerivativeAttemptOutcome.Completed,
                boundedReason, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            graphConvergenceSignal?.Signal(job.GraphExecutionId);
            dbContext.ChangeTracker.Clear();
            return;
        }
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        var completed = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == jobId, cancellationToken).ConfigureAwait(false);
        if (completed?.Status == CentralDerivativeJobStatus.Completed && completed.ResultCentralArtifactId is null)
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
            await FinalizeTransientSlotsAsync(jobId, cancellationToken).ConfigureAwait(false);
            var terminalReason = retryable
                ? CentralTransientRuntimeReasonCodes.AttemptsExhausted
                : error.Length <= 256 ? error : error[..256];
            await CentralTransientValidationOutcome.RecordNeedsReviewAsync(
                dbContext, jobId, terminalReason, now, cancellationToken).ConfigureAwait(false);
        }
        await CompleteAttemptAsync(
            jobId,
            job.AttemptCount,
            terminal ? CentralDerivativeAttemptOutcome.TerminalFailure : CentralDerivativeAttemptOutcome.RetryableFailure,
            retryable ? "processing.retryable-failure" : "processing.terminal-failure",
            now,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (terminal)
        {
            graphConvergenceSignal?.Signal(job.GraphExecutionId);
        }
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
        graphConvergenceSignal?.Signal(job.GraphExecutionId);
        dbContext.ChangeTracker.Clear();
    }

    public async Task MarkInputUnavailableAsync(
        Guid jobId,
        Guid leaseToken,
        Guid centralArtifactId,
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
            .Include(candidate => candidate.Inputs).ThenInclude(input => input.Artifact)
            .SingleOrDefaultAsync(candidate => candidate.Id == jobId
                && candidate.Status == CentralDerivativeJobStatus.Leased
                && candidate.LeaseToken == leaseToken
                && candidate.LeaseExpiresAtUtc > now,
                cancellationToken).ConfigureAwait(false)
            ?? throw new CentralDerivativeJobStateException("The derivative job lease is stale or invalid.");
        var source = job.Inputs.SingleOrDefault(input => input.CentralArtifactId == centralArtifactId)?.Artifact
            ?? throw new CentralDerivativeJobStateException("The derivative input is not part of the leased source set.");
        if (!source.RowVersion.AsSpan().SequenceEqual(expectedSourceRowVersion))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new CentralDerivativeJobStateException(
                "The derivative source changed after object verification.");
        }
        var activeStatuses = new[]
        {
            CentralDerivativeJobStatus.Waiting,
            CentralDerivativeJobStatus.Pending,
            CentralDerivativeJobStatus.Leased,
            CentralDerivativeJobStatus.RetryableFailure,
            CentralDerivativeJobStatus.Completed
        };
        var affectedJobs = await dbContext.CentralDerivativeJobs.AsNoTracking()
            .Where(candidate => (candidate.SourceCentralArtifactId == source.Id
                    || candidate.Inputs.Any(input => input.CentralArtifactId == source.Id))
                && activeStatuses.Contains(candidate.Status))
            .Select(candidate => new { candidate.Id, GraphOwned = candidate.GraphExecutionId != null })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var affectedJobIds = affectedJobs.Select(candidate => candidate.Id).ToList();
        // Graph-owned nodes are terminalized (or deliberately left as recorded) by InvalidateDependentsAsync with
        // frozen identities; reopening them here as RetryableFailure would strand a live execution behind a job that
        // can never be reclaimed, and TR_CentralDerivativeJobs_GraphIdentityImmutable rejects reopening a node of a
        // terminal execution. Only legacy scheduler jobs take the bulk reopen below.
        var legacyJobIds = affectedJobs.Where(candidate => !candidate.GraphOwned)
            .Select(candidate => candidate.Id)
            .ToList();
        source.ObjectState = quarantine
            ? CentralArtifactObjectState.Quarantined
            : CentralArtifactObjectState.Pending;
        source.ReconstructionState = quarantine
            ? CentralReconstructionState.Quarantined
            : CentralReconstructionState.PendingReference;
        source.StateReasonCode = reasonCode;
        source.ReconciledAtUtc = null;
        await ArtifactIngestService.InvalidateDependentsAsync(dbContext, source, graphConvergenceSignal, cancellationToken)
            .ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await dbContext.CentralDerivativeJobs.Where(candidate => legacyJobIds.Contains(candidate.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.Status, candidate =>
                    !quarantine && candidate.Status == CentralDerivativeJobStatus.Waiting
                        ? CentralDerivativeJobStatus.Waiting
                        : quarantine
                            ? CentralDerivativeJobStatus.Quarantined
                            : CentralDerivativeJobStatus.RetryableFailure)
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
                legacyJobIds.Contains(attempt.CentralDerivativeJobId)
                && attempt.Outcome == CentralDerivativeAttemptOutcome.Leased)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(attempt => attempt.Outcome, quarantine
                    ? CentralDerivativeAttemptOutcome.Quarantined
                    : CentralDerivativeAttemptOutcome.RetryableFailure)
                .SetProperty(attempt => attempt.ReasonCode, reasonCode)
                .SetProperty(attempt => attempt.EndedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
        if (quarantine)
        {
            foreach (var affectedJobId in affectedJobIds)
            {
                await FinalizeTransientSlotsAsync(affectedJobId, cancellationToken).ConfigureAwait(false);
                await CentralTransientValidationOutcome.RecordNeedsReviewAsync(
                    dbContext, affectedJobId, reasonCode, now, cancellationToken).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (quarantine && job.Inputs.Count > 1)
        {
            telemetry?.RecordWindowPinDuration(
                job.RecipeName, now - job.Inputs.Min(input => input.SelectedAtUtc), "quarantined");
        }
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
            .Include(job => job.GraphExecution)
            .Include(job => job.SourceArtifact)!.ThenInclude(artifact => artifact!.Frame)
            .Include(job => job.Inputs).ThenInclude(input => input.Artifact)!.ThenInclude(artifact => artifact!.Frame)
            .Include(job => job.Inputs).ThenInclude(input => input.Requirement)
            .Include(job => job.CanonicalInputs).ThenInclude(input => input.Requirement)
            .Include(job => job.ResultArtifact)
            .AsSplitQuery()
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
                .SetProperty(artifact => artifact.ReconciledAtUtc, now)
                .SetProperty(artifact => artifact.ObjectVerificationToken, (Guid?)null)
                .SetProperty(artifact => artifact.ObjectVerificationRequestedAtUtc, (DateTimeOffset?)null)
                .SetProperty(artifact => artifact.ObjectVerificationRetryCount, 0)
                .SetProperty(artifact => artifact.ObjectVerificationRetryAtUtc, (DateTimeOffset?)null), cancellationToken);

    private Task<int> FinalizeTransientSlotsAsync(Guid jobId, CancellationToken cancellationToken)
        => dbContext.CentralTransientValidationIdentitySlots.Where(slot =>
                slot.CentralDerivativeJobId == jobId
                && slot.State == CentralTransientValidationIdentitySlotState.Reserved)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                slot => slot.State, CentralTransientValidationIdentitySlotState.Unused), cancellationToken);

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
        if (job.GraphExecutionId is not null && !string.Equals(
                job.InputSetIdentitySha256,
                CentralDerivativeWindowIdentity.CreateInputSetIdentity(job.Inputs, job.CanonicalInputs),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDerivativeJobStateException("The derivative graph frozen input identity is inconsistent.");
        }
        var inputs = job.Inputs.OrderBy(input => input.Ordinal).Select(input =>
        {
            var artifact = input.Artifact ?? throw new InvalidOperationException("A derivative input artifact was not loaded.");
            var inputFrame = artifact.Frame ?? throw new InvalidOperationException("A derivative input frame was not loaded.");
            return new CentralDerivativeJobLeaseInput(
                input.Ordinal,
                artifact.Id,
                inputFrame.DevicePublicId,
                artifact.ArtifactId,
                artifact.Role,
                artifact.RecipeVersion,
                artifact.ChecksumSha256,
                artifact.MediaType,
                artifact.ByteLength,
                inputFrame.FrameId,
                inputFrame.AgentId,
                inputFrame.CaptureSequence,
                inputFrame.CapturedAtUtc,
                input.CompatibilitySha256,
                input.SelectedAtUtc,
                input.Requirement?.BindingName ?? "input",
                input.Requirement?.GraphInputBindingKind ??
                    (string.Equals(input.Requirement?.BindingName, "input", StringComparison.Ordinal)
                        ? ProcessingGraphInputBindingKind.PrimaryArtifact
                        : ProcessingGraphInputBindingKind.AuxiliaryArtifact));
        }).ToArray();
        var canonicalInputs = job.CanonicalInputs.OrderBy(input => input.Ordinal).Select(input =>
            new CentralDerivativeJobLeaseCanonicalInput(
                input.Ordinal,
                input.Requirement?.BindingName ?? throw new InvalidOperationException(
                    "A canonical derivative input requirement was not loaded."),
                input.SchemaVersion,
                input.IdentitySha256,
                input.CanonicalJson,
                input.ByteLength,
                input.EnvironmentalObservationRecordId,
                input.SelectedAtUtc)).ToArray();
        return new CentralDerivativeJobLease(
            job.Id, job.LeaseToken!.Value, job.LeaseOwner!, job.LeaseExpiresAtUtc!.Value,
            frame.DevicePublicId, source.ArtifactId, source.Role, source.RecipeVersion,
            $"/api/v1.0/devices/{frame.DevicePublicId:D}/artifacts/{source.ArtifactId:D}/content",
            source.ChecksumSha256, source.MediaType, frame.FrameId, frame.AgentId,
            frame.CapturedAtUtc, frame.RigProfileVersion,
            CentralDerivativeJobExecutor.ResolveLeaseSceneProvenance(
                job.GraphExecutionId, job.RecipeName, job.RequestedRecipeIdentitySha256,
                job.ExpectedRecipeIdentitySha256, frame.SceneProvenanceJson),
            job.TargetRole, job.TargetRecipeVersion, job.TargetVariant,
            job.RecipeName, job.RecipeOptionsJson, job.InputSelectorJson,
            job.RequestedRecipeIdentitySha256, job.RequestIdentitySha256,
            job.TraceParent, job.TraceState,
            job.AttemptCount, job.MaxAttempts,
            inputs,
            job.ExpectedRecipeIdentitySha256,
            canonicalInputs,
            job.InputSetIdentitySha256,
            job.GraphExecutionId,
            job.GraphExecution?.RevisionId,
            job.GraphNodeId,
            job.GraphNodeOrdinal,
            job.SharedNodePlanIdentitySha256,
            job.FrozenNodePlanJson,
            job.GraphExecution?.CentralPlanIdentitySha256,
            job.GraphExecution?.FrozenCentralPlanJson,
            job.GraphExecution?.DefinitionIdentitySha256,
            job.GraphExecution?.FrozenDefinitionJson);
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
    int MaxAttempts,
    IReadOnlyList<CentralDerivativeJobLeaseInput>? Inputs = null,
    string? ExpectedRecipeIdentitySha256 = null,
    IReadOnlyList<CentralDerivativeJobLeaseCanonicalInput>? CanonicalInputs = null,
    string? InputSetIdentitySha256 = null,
    Guid? GraphExecutionId = null,
    Guid? GraphRevisionId = null,
    string? GraphNodeId = null,
    int? GraphNodeOrdinal = null,
    string? SharedNodePlanIdentitySha256 = null,
    string? FrozenNodePlanJson = null,
    string? CentralPlanIdentitySha256 = null,
    string? FrozenCentralPlanJson = null,
    string? GraphDefinitionIdentitySha256 = null,
    string? FrozenDefinitionJson = null);

internal sealed record CentralDerivativeJobLeaseInput(
    int Ordinal,
    Guid CentralArtifactId,
    Guid DevicePublicId,
    Guid ArtifactId,
    FrameArtifactRole Role,
    string RecipeVersion,
    string ChecksumSha256,
    string MediaType,
    long ByteLength,
    Guid FrameId,
    string AgentId,
    long? CaptureSequence,
    DateTimeOffset CapturedAtUtc,
    string CompatibilitySha256,
    DateTimeOffset SelectedAtUtc = default,
    string BindingName = "input",
    ProcessingGraphInputBindingKind BindingKind = ProcessingGraphInputBindingKind.PrimaryArtifact);

internal sealed record CentralDerivativeJobLeaseCanonicalInput(
    int Ordinal,
    string BindingName,
    string SchemaVersion,
    string IdentitySha256,
    string CanonicalJson,
    int ByteLength,
    Guid? EnvironmentalObservationRecordId,
    DateTimeOffset SelectedAtUtc);

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

/// <summary>Lease renewal was refused because cancellation was requested for the leased node or its graph execution.</summary>
internal sealed class CentralDerivativeLeaseCanceledException : Exception
{
    public CentralDerivativeLeaseCanceledException()
        : base("The derivative job lease cannot be renewed because cancellation was requested.")
    {
    }

    public CentralDerivativeLeaseCanceledException(string message) : base(message)
    {
    }

    public CentralDerivativeLeaseCanceledException(string message, Exception innerException) : base(message, innerException)
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
