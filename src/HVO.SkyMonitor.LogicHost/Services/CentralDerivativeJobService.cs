using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using System.Text.Json;

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

/// <summary>How a claimer relates to observatory runner pools (#429).</summary>
internal enum CentralDerivativeClaimPoolMode
{
    /// <summary>Serves only observatories without a dedicated pool (in-process worker, unpooled runners).</summary>
    Shared,

    /// <summary>Serves its pool's observatories first, then shared work.</summary>
    Reserved,

    /// <summary>Serves only its pool's observatories.</summary>
    Dedicated
}

/// <summary>Restricts a claim to (or away from) a recipe set; names must be built-in recipe names.</summary>
internal sealed record CentralDerivativeClaimScope(
    IReadOnlySet<string> Recipes,
    bool Include,
    long? MaximumInputBytes = null,
    string? Pool = null,
    CentralDerivativeClaimPoolMode PoolMode = CentralDerivativeClaimPoolMode.Shared)
{
    public static CentralDerivativeClaimScope Only(
        IEnumerable<string> recipes,
        long? maximumInputBytes = null,
        string? pool = null,
        CentralDerivativeClaimPoolMode poolMode = CentralDerivativeClaimPoolMode.Shared)
        => new(recipes.ToHashSet(StringComparer.Ordinal), true, maximumInputBytes, pool, poolMode);

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

internal sealed partial class CentralDerivativeJobService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider,
    CentralDerivativeWorkerTelemetry? telemetry = null,
    CentralProcessingGraphConvergenceSignal? graphConvergenceSignal = null,
    IOptions<CentralProcessingRunnerOptions>? runnerOptions = null,
    IOptions<CentralProcessingEntitlementOptions>? entitlementOptions = null,
    CentralProcessingFairnessTelemetry? fairnessTelemetry = null,
    ILogger<CentralDerivativeJobService>? logger = null)
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
        var entitlements = entitlementOptions?.Value;
        var fairness = entitlements is { Enabled: true };
        var poolMode = scope?.PoolMode ?? CentralDerivativeClaimPoolMode.Shared;
        var pool = scope?.Pool ?? string.Empty;
        var excludedObservatories = new List<Guid>();
        var excludedCameras = new List<Guid>();
        var excludedClasses = new List<string>();
        var excludedObservatoryClasses = new List<(Guid ObservatoryId, string ResourceClass)>();
        var excludedJobs = new List<Guid>();
        var lockBusy = 0;
        var retryReasons = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Retry(string reason) => retryReasons[reason] = retryReasons.GetValueOrDefault(reason) + 1;
        var claimStarted = timeProvider.GetTimestamp();
        for (var collision = 0; collision < 100; collision++)
        {
            if (collision == ContentionLogThreshold && logger is not null)
            {
                Log.ClaimContention(logger, workerId, collision, lockBusy,
                    excludedObservatories.Count + excludedCameras.Count + excludedClasses.Count + excludedObservatoryClasses.Count, excludedJobs.Count,
                    string.Join(',', retryReasons.Select(pair => $"{pair.Key}={pair.Value}")),
                    timeProvider.GetElapsedTime(claimStarted).TotalMilliseconds);
            }
            var now = timeProvider.GetUtcNow();
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            // Text parameters are declared nvarchar(max) so that changing JSON/list lengths reuse one cached plan
            // instead of compiling a plan per distinct length.
            SqlParameter[] CreateParameters() =>
            [
                    new SqlParameter("@now", now),
                    Text("@includeRecipes", includeRecipes),
                    Text("@excludeRecipes", excludeRecipes),
                    new SqlParameter("@maximumInputBytes", maximumInputBytes),
                    Text("@entitlements", entitlements?.CreateObservatoryEntitlementsJson() ?? "[]"),
                    Text("@classes", entitlements?.CreateRecipeClassesJson() ?? "[]"),
                    Text("@classLimits", entitlements?.CreateObservatoryClassLimitsJson() ?? "[]"),
                    new SqlParameter("@defaultActive", entitlements?.DefaultActiveJobs ?? 0),
                    new SqlParameter("@defaultCamera", entitlements?.DefaultActiveJobsPerCamera ?? 0),
                    new SqlParameter("@defaultWeight", entitlements?.DefaultWeight ?? 1.0),
                    new SqlParameter("@defaultPriority", entitlements?.DefaultPriority ?? 0),
                    new SqlParameter("@starvationBefore", CentralProcessingEntitlementOptions.StarvationThreshold(now, entitlements?.StarvationAge ?? TimeSpan.FromMinutes(10))),
                    new SqlParameter("@servedSince", CentralProcessingEntitlementOptions.StarvationThreshold(now, entitlements?.FairShareWindow ?? TimeSpan.FromMinutes(10))),
                    Text("@pool", pool),
                    new SqlParameter("@poolMode", (int)poolMode),
                    Text("@excluded", JsonSerializer.Serialize(excludedObservatories)),
                    Text("@excludedCameras", JsonSerializer.Serialize(excludedCameras)),
                    Text("@excludedClasses", JsonSerializer.Serialize(excludedClasses)),
                    Text("@excludedObservatoryClasses", JsonSerializer.Serialize(excludedObservatoryClasses.Select(pair => new { o = pair.ObservatoryId, cls = pair.ResourceClass }))),
                    Text("@excludedJobs", JsonSerializer.Serialize(excludedJobs))
            ];
            static SqlParameter Text(string name, string value) =>
                new(name, System.Data.SqlDbType.NVarChar, -1) { Value = value };
            CentralDerivativeJob? candidate;
            EntitlementRejection? rejection = null;
            if (fairness)
            {
                // Fair ordering sorts every qualifying row, and a sort under UPDLOCK would hold update locks on all of
                // them until commit, starving concurrent claimers through READPAST. Rank without lock hints, then lock
                // and re-validate one row at a time from a small batch ordered breadth-first across observatories.
                // A row another claimer holds is skipped by READPAST, a row another claimer already leased fails the
                // status predicate, and an observatory another claimer is deciding right now (its entitlement lock is
                // busy) is skipped for the next-ranked row instead of queueing behind a decision made on stale rank.
                var candidateIds = await SelectCandidateIdsAsync(CreateCandidateSql(fairness: true, idOnly: true), CreateParameters(), cancellationToken)
                    .ConfigureAwait(false);
                candidate = null;
                var busyInBatch = 0;
                foreach (var candidateId in candidateIds)
                {
                    // Each candidate is tried under a savepoint so a skipped row's update lock (and nothing else) is
                    // released at once; otherwise other claimers' READPAST re-reads would treat it as taken.
                    await transaction.CreateSavepointAsync("candidate", cancellationToken).ConfigureAwait(false);
                    var row = await dbContext.CentralDerivativeJobs
                        .FromSqlRaw("""
                            SELECT TOP(1) job.* FROM [CentralDerivativeJobs] AS job WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK, ROWLOCK)
                            WHERE job.[Id] = @candidateId
                              AND ((job.[Status] IN (N'Pending', N'RetryableFailure') AND job.[AvailableAtUtc] <= @now)
                                   OR (job.[Status] = N'Leased' AND job.[LeaseExpiresAtUtc] <= @now))
                            """,
                            new SqlParameter("@candidateId", candidateId),
                            new SqlParameter("@now", now))
                        .AsNoTracking()
                        .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                    if (row is null)
                    {
                        // Leased meanwhile (the next ranking omits it) or briefly held by another claimer's decision
                        // (the next ranking returns it), so it is not excluded for the rest of the call.
                        await transaction.RollbackToSavepointAsync("candidate", cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    // An expired lease whose attempts are exhausted only terminalizes stale work; it consumes no
                    // capacity and is exempt from the entitlement re-check (and from the fairness predicates).
                    if (!IsTerminalCleanup(row, now))
                    {
                        // The candidate query filtered on counts read without locks; serialize claims per observatory
                        // with a transaction-scoped application lock and re-check before leasing so concurrent claims
                        // can never exceed an entitlement.
                        rejection = await RecheckEntitlementAsync(row, entitlements!, now, cancellationToken).ConfigureAwait(false);
                        if (rejection is { Reason: "lock-busy" })
                        {
                            // Transient: the row stays eligible for a later iteration once that claimer has decided.
                            lockBusy++;
                            busyInBatch++;
                            rejection = null;
                            await transaction.RollbackToSavepointAsync("candidate", cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                    }
                    candidate = row;
                    break;
                }
                if (candidateIds.Count > 0 && candidate is null)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    dbContext.ChangeTracker.Clear();
                    Retry(busyInBatch > 0 ? "lock-busy" : "batch-exhausted");
                    if (busyInBatch > 0)
                    {
                        // Every remaining candidate belongs to an observatory another claimer is deciding; give that
                        // decision a moment to commit before ranking again.
                        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(100, 5 * (collision + 1))), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    continue;
                }
            }
            else
            {
                candidate = await dbContext.CentralDerivativeJobs
                    .FromSqlRaw(CreateCandidateSql(fairness: false), CreateParameters())
                    .AsNoTracking()
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }
            if (candidate is null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return null;
            }
            if (rejection is { } throttled)
            {
                // A rejection excludes only the saturated dimension (observatory, camera, class, or observatory-class
                // pair) for the rest of this claim call so compatible work stays eligible.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                switch (throttled.Reason)
                {
                    case "camera":
                        excludedCameras.Add(throttled.DevicePublicId);
                        break;
                    case "class":
                    case "class-bytes":
                        excludedClasses.Add(throttled.ResourceClass);
                        break;
                    case "observatory-class":
                        excludedObservatoryClasses.Add((throttled.ObservatoryId, throttled.ResourceClass));
                        break;
                    default:
                        excludedObservatories.Add(throttled.ObservatoryId);
                        break;
                }
                fairnessTelemetry?.RecordThrottled(throttled.ObservatoryId, throttled.Reason);
                if (logger is not null)
                {
                    Log.Throttled(logger, workerId, candidate.Id, throttled.ObservatoryId, throttled.Reason, throttled.Active, throttled.Limit);
                }
                continue;
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
                    if (adoptedAttempt == 1)
                    {
                        await RecordUsageAsync(candidate.Id, candidate.AttemptCount, cancellationToken).ConfigureAwait(false);
                    }
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
                    Retry("adopted-outcome");
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
                if (expiredAttempt == 1)
                {
                    await RecordUsageAsync(candidate.Id, candidate.AttemptCount, cancellationToken).ConfigureAwait(false);
                }
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
                    Retry("expired-attempt");
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
                        Retry("terminal-update");
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
                // The authoritative predicate rejected the locked row (its inputs or requirements changed under it);
                // leave it out of this call's later rankings rather than re-selecting it.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                dbContext.ChangeTracker.Clear();
                excludedJobs.Add(candidate.Id);
                Retry("lease-update");
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
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            // The lease is ours after commit; loading it outside the transaction keeps the observatory lock short.
            var claimed = await LoadJobAsync(candidate.Id, cancellationToken).ConfigureAwait(false);
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
        if (entitlementOptions?.Value is { Enabled: true } renewalEntitlements)
        {
            // Renewal updates a counted lease row; it takes the same entitlement locks a claim takes (observatory,
            // then the resource class when that class has a budget), waiting unlike claims, so the wait-free
            // re-check of a concurrent claim in any observatory never sees this lease as an in-flight row.
            var renewing = await dbContext.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.Id == jobId)
                .Select(job => new { job.SourceArtifact!.Frame!.ObservatoryId, job.RecipeName })
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (renewing is not null)
            {
                var resourceClass = renewalEntitlements.ResolveResourceClass(renewing.RecipeName);
                var classHasBudget = renewalEntitlements.ResourceClasses.TryGetValue(resourceClass, out var classBudget)
                    && (classBudget.ActiveJobs > 0 || classBudget.ActiveInputBytes > 0);
                if (!await TryAcquireEntitlementLockAsync($"hvo-entitlement:{renewing.ObservatoryId:N}", RenewalLockTimeout, cancellationToken).ConfigureAwait(false)
                    || (classHasBudget
                        && !await TryAcquireEntitlementLockAsync($"hvo-entitlement:class:{resourceClass}", RenewalLockTimeout, cancellationToken).ConfigureAwait(false)))
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    throw new CentralDerivativeJobStateException("The entitlement lock could not be acquired for lease renewal.");
                }
            }
        }
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
        await RecordUsageAsync(jobId, attemptNumber, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the auditable usage row for a terminal attempt in the caller's transaction (idempotent per attempt).
    /// Completion and usage signals are derived from the committed rows by the worker's sampling, never from an
    /// open transaction.
    /// </summary>
    private Task<int> RecordUsageAsync(Guid jobId, int attemptNumber, CancellationToken cancellationToken)
        => CentralProcessingUsageRecorder.RecordAsync(dbContext, entitlementOptions?.Value, jobId, attemptNumber, cancellationToken);

    private sealed record EntitlementRejection(
        Guid ObservatoryId, Guid DevicePublicId, string ResourceClass, string Reason, long Active, long Limit);

    private sealed record EntitlementCheckRow(
        Guid ObservatoryId,
        Guid DevicePublicId,
        string ResourceClass,
        int ObservatoryActive,
        int CameraActive,
        int ClassActive,
        long ClassActiveBytes,
        int ObservatoryClassActive,
        long CandidateBytes);

    /// <summary>
    /// Runs the fairness candidate query as a raw command inside the current transaction. The query starts with a
    /// common table expression, which EF Core cannot compose over, so it bypasses the query pipeline.
    /// </summary>
    private async Task<List<Guid>> SelectCandidateIdsAsync(string sql, SqlParameter[] parameters, CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The text is the constant CreateCandidateSql template; every runtime value is a SqlParameter.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandTimeout = dbContext.Database.GetCommandTimeout() ?? command.CommandTimeout;
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter);
        }
        var ids = new List<Guid>(FairCandidateBatchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }
        return ids;
    }

    /// <summary>
    /// Number of fair-ordered candidates fetched per claim attempt. Bounds how many concurrent claimers can be
    /// absorbed without a collision round trip; a claimer that exhausts the batch re-queries with them excluded.
    /// </summary>
    internal const int FairCandidateBatchSize = 16;

    private static bool IsTerminalCleanup(CentralDerivativeJob candidate, DateTimeOffset now)
        => candidate.Status == CentralDerivativeJobStatus.Leased
            && candidate.LeaseExpiresAtUtc <= now
            && candidate.AttemptCount >= candidate.MaxAttempts;

    /// <summary>
    /// Takes an entitlement lock without waiting. A busy lock means another claimer is deciding that dimension right
    /// now; the caller moves on to its next-ranked candidate rather than queueing behind a rank it computed earlier.
    /// </summary>
    private Task<bool> TryAcquireEntitlementLockAsync(string resource, CancellationToken cancellationToken)
        => TryAcquireEntitlementLockAsync(resource, TimeSpan.Zero, cancellationToken);

    /// <summary>Lease renewal waits for the observatory lock (claim decisions hold it for milliseconds).</summary>
    internal static readonly TimeSpan RenewalLockTimeout = TimeSpan.FromSeconds(30);

    private async Task<bool> TryAcquireEntitlementLockAsync(string resource, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = new SqlParameter("@result", System.Data.SqlDbType.Int) { Direction = System.Data.ParameterDirection.Output };
        await dbContext.Database.ExecuteSqlRawAsync("""
                EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = @timeout;
                """, [new SqlParameter("@resource", resource), new SqlParameter("@timeout", (int)timeout.TotalMilliseconds), result], cancellationToken).ConfigureAwait(false);
        return result.Value is int value && value >= 0;
    }

    private Task<int> ReleaseEntitlementLockAsync(string resource, CancellationToken cancellationToken)
        => dbContext.Database.ExecuteSqlRawAsync(
            "EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Transaction';",
            [new SqlParameter("@resource", resource)], cancellationToken);

    private async Task<EntitlementRejection?> RecheckEntitlementAsync(
        CentralDerivativeJob candidate,
        CentralProcessingEntitlementOptions entitlements,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var classes = entitlements.CreateRecipeClassesJson();
        var identity = await dbContext.Database.SqlQueryRaw<EntitlementIdentityRow>("""
                SELECT frame.[ObservatoryId] AS [ObservatoryId], frame.[DevicePublicId] AS [DevicePublicId]
                FROM [CentralArtifacts] AS source
                INNER JOIN [CentralFrames] AS frame ON frame.[Id] = source.[CentralFrameId]
                WHERE source.[Id] = @sourceId
                """, new SqlParameter("@sourceId", candidate.SourceCentralArtifactId))
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        // Claims are serialized per observatory (observatory and camera counts) and, when the recipe's class carries
        // a budget, per class (class-wide counts). Locks are always taken observatory first, then class, so claimers
        // never wait on each other in a cycle. The counts below are wait-free (READPAST): a re-check that waited on
        // row locks under the entitlement lock could deadlock with any transaction that touches attempt or job
        // rows. Skipping locked rows is exact under the lock protocol: every claimer that could add a lease to the
        // counted dimension holds the same entitlement lock and has committed, lease renewal takes the observatory
        // lock as well (so a renewing lease is never in flight during a re-check), and a row being completed,
        // skipped, failed, or expired is leaving the active set.
        var resourceClass = entitlements.ResolveResourceClass(candidate.RecipeName);
        var observatoryLock = $"hvo-entitlement:{identity.ObservatoryId:N}";
        if (!await TryAcquireEntitlementLockAsync(observatoryLock, cancellationToken).ConfigureAwait(false))
        {
            return new EntitlementRejection(identity.ObservatoryId, identity.DevicePublicId, resourceClass, "lock-busy", 0, 0);
        }
        if (entitlements.ResourceClasses.TryGetValue(resourceClass, out var classBudget)
            && (classBudget.ActiveJobs > 0 || classBudget.ActiveInputBytes > 0)
            && !await TryAcquireEntitlementLockAsync($"hvo-entitlement:class:{resourceClass}", cancellationToken).ConfigureAwait(false))
        {
            await ReleaseEntitlementLockAsync(observatoryLock, cancellationToken).ConfigureAwait(false);
            return new EntitlementRejection(identity.ObservatoryId, identity.DevicePublicId, resourceClass, "lock-busy", 0, 0);
        }
        var counts = await dbContext.Database.SqlQueryRaw<EntitlementCheckRow>("""
                SELECT @observatoryId AS [ObservatoryId], @deviceId AS [DevicePublicId], COALESCE(rc.[cls], N'image') AS [ResourceClass],
                    (SELECT COUNT(*) FROM [CentralDerivativeJobs] AS a WITH (READPAST)
                     INNER JOIN [CentralArtifacts] AS sa ON sa.[Id] = a.[SourceCentralArtifactId]
                     INNER JOIN [CentralFrames] AS fa ON fa.[Id] = sa.[CentralFrameId]
                     WHERE a.[Status] = N'Leased' AND a.[LeaseExpiresAtUtc] > @now AND fa.[ObservatoryId] = @observatoryId) AS [ObservatoryActive],
                    (SELECT COUNT(*) FROM [CentralDerivativeJobs] AS a WITH (READPAST)
                     INNER JOIN [CentralArtifacts] AS sa ON sa.[Id] = a.[SourceCentralArtifactId]
                     INNER JOIN [CentralFrames] AS fa ON fa.[Id] = sa.[CentralFrameId]
                     WHERE a.[Status] = N'Leased' AND a.[LeaseExpiresAtUtc] > @now AND fa.[DevicePublicId] = @deviceId) AS [CameraActive],
                    (SELECT COUNT(*) FROM [CentralDerivativeJobs] AS a WITH (READPAST)
                     INNER JOIN OPENJSON(@classes) WITH ([r] nvarchar(128) '$.r', [cls] nvarchar(64) '$.cls') AS rc2 ON rc2.[r] = a.[RecipeName]
                     WHERE a.[Status] = N'Leased' AND a.[LeaseExpiresAtUtc] > @now AND rc2.[cls] = COALESCE(rc.[cls], N'image')) AS [ClassActive],
                    (SELECT COALESCE(SUM(sz.[ByteLength]), 0) FROM [CentralDerivativeJobs] AS a WITH (READPAST)
                     INNER JOIN OPENJSON(@classes) WITH ([r] nvarchar(128) '$.r', [cls] nvarchar(64) '$.cls') AS rc3 ON rc3.[r] = a.[RecipeName]
                     INNER JOIN [CentralDerivativeJobInputs] AS i ON i.[CentralDerivativeJobId] = a.[Id]
                     INNER JOIN [CentralArtifacts] AS sz ON sz.[Id] = i.[CentralArtifactId]
                     WHERE a.[Status] = N'Leased' AND a.[LeaseExpiresAtUtc] > @now AND rc3.[cls] = COALESCE(rc.[cls], N'image')) AS [ClassActiveBytes],
                    (SELECT COUNT(*) FROM [CentralDerivativeJobs] AS a WITH (READPAST)
                     INNER JOIN [CentralArtifacts] AS sa ON sa.[Id] = a.[SourceCentralArtifactId]
                     INNER JOIN [CentralFrames] AS fa ON fa.[Id] = sa.[CentralFrameId]
                     INNER JOIN OPENJSON(@classes) WITH ([r] nvarchar(128) '$.r', [cls] nvarchar(64) '$.cls') AS rc4 ON rc4.[r] = a.[RecipeName]
                     WHERE a.[Status] = N'Leased' AND a.[LeaseExpiresAtUtc] > @now AND fa.[ObservatoryId] = @observatoryId
                       AND rc4.[cls] = COALESCE(rc.[cls], N'image')) AS [ObservatoryClassActive],
                    (SELECT COALESCE(SUM(cb.[ByteLength]), 0) FROM [CentralDerivativeJobInputs] AS ci
                     INNER JOIN [CentralArtifacts] AS cb ON cb.[Id] = ci.[CentralArtifactId]
                     WHERE ci.[CentralDerivativeJobId] = @jobId) AS [CandidateBytes]
                FROM (SELECT @recipe AS [RecipeName]) AS candidate
                LEFT JOIN OPENJSON(@classes) WITH ([r] nvarchar(128) '$.r', [cls] nvarchar(64) '$.cls') AS rc ON rc.[r] = candidate.[RecipeName]
                """,
                new SqlParameter("@observatoryId", identity.ObservatoryId),
                new SqlParameter("@deviceId", identity.DevicePublicId),
                new SqlParameter("@now", now),
                new SqlParameter("@classes", System.Data.SqlDbType.NVarChar, -1) { Value = classes },
                new SqlParameter("@jobId", candidate.Id),
                new SqlParameter("@recipe", candidate.RecipeName))
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var observatoryLimit = entitlements.ResolveActiveJobs(identity.ObservatoryId);
        if (observatoryLimit > 0 && counts.ObservatoryActive >= observatoryLimit)
        {
            return new EntitlementRejection(identity.ObservatoryId, identity.DevicePublicId, resourceClass, "observatory", counts.ObservatoryActive, observatoryLimit);
        }
        var cameraLimit = entitlements.ResolveActiveJobsPerCamera(identity.ObservatoryId);
        if (cameraLimit > 0 && counts.CameraActive >= cameraLimit)
        {
            return new EntitlementRejection(identity.ObservatoryId, identity.DevicePublicId, resourceClass, "camera", counts.CameraActive, cameraLimit);
        }
        if (entitlements.ResourceClasses.TryGetValue(counts.ResourceClass, out var budget))
        {
            if (budget.ActiveJobs > 0 && counts.ClassActive >= budget.ActiveJobs)
            {
                return new EntitlementRejection(identity.ObservatoryId, identity.DevicePublicId, resourceClass, "class", counts.ClassActive, budget.ActiveJobs);
            }
            if (budget.ActiveInputBytes > 0 && counts.ClassActiveBytes + counts.CandidateBytes > budget.ActiveInputBytes)
            {
                return new EntitlementRejection(identity.ObservatoryId, identity.DevicePublicId, resourceClass, "class-bytes", counts.ClassActiveBytes + counts.CandidateBytes, budget.ActiveInputBytes);
            }
        }
        var observatoryClassLimit = entitlements.Find(identity.ObservatoryId)?.ResourceClassActiveJobs;
        if (observatoryClassLimit is not null
            && observatoryClassLimit.TryGetValue(counts.ResourceClass, out var perClass)
            && perClass > 0 && counts.ObservatoryClassActive >= perClass)
        {
            return new EntitlementRejection(identity.ObservatoryId, identity.DevicePublicId, resourceClass, "observatory-class", counts.ObservatoryClassActive, perClass);
        }
        return null;
    }

    private sealed record EntitlementIdentityRow(Guid ObservatoryId, Guid DevicePublicId);

    /// <summary>Claim iterations after which a single claim call reports contention (event 2221).</summary>
    internal const int ContentionLogThreshold = 8;

    private static partial class Log
    {
        [LoggerMessage(2220, LogLevel.Information,
            "Central derivative claim throttled: Worker={Worker}, JobId={JobId}, Observatory={Observatory}, Reason={Reason}, Active={Active}, Limit={Limit}")]
        public static partial void Throttled(
            ILogger logger, string worker, Guid jobId, Guid observatory, string reason, long active, long limit);

        [LoggerMessage(2221, LogLevel.Warning,
            "Central derivative claim contention: Worker={Worker} is on claim iteration {Iteration} after {ElapsedMilliseconds:F0} ms (LockBusy={LockBusy}, ThrottledDimensions={ThrottledDimensions}, SkippedJobs={SkippedJobs}, Retries={Retries})")]
        public static partial void ClaimContention(
            ILogger logger, string worker, int iteration, int lockBusy, int throttledDimensions, int skippedJobs, string retries, double elapsedMilliseconds);
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

    /// <summary>
    /// The candidate query. The base predicate is unchanged from the pre-#429 claim; when fairness is enabled the
    /// query additionally joins the source frame's observatory and camera, the configured entitlements and recipe
    /// classes, applies the entitlement, pool, and exclusion predicates, and orders by starvation, pool affinity,
    /// priority, and weighted fair share before the original availability order.
    /// </summary>
    /// <summary>Recipe include/exclude and the input-size bound, shared by the claim and the elastic backlog count.</summary>
    private const string RecipeAndSizeWhere = """
                (@includeRecipes = N'' OR job.[RecipeName] IN (SELECT [value] FROM STRING_SPLIT(@includeRecipes, ',')))
                AND (@excludeRecipes = N'' OR job.[RecipeName] NOT IN (SELECT [value] FROM STRING_SPLIT(@excludeRecipes, ',')))
                AND ((SELECT COALESCE(SUM(sized.[ByteLength]), 0)
                      FROM [CentralDerivativeJobInputs] AS sizedInput
                      INNER JOIN [CentralArtifacts] AS sized ON sized.[Id] = sizedInput.[CentralArtifactId]
                      WHERE sizedInput.[CentralDerivativeJobId] = job.[Id]) <= @maximumInputBytes)
        """;

    /// <summary>
    /// Readiness as the claim sees it: a running graph execution when expanded, a resolved input set with every
    /// required input present and available (or an exempt missing input), inputs whose objects are available and
    /// reconstructed, and either claimable work (pending, retryable, or an expired lease with attempts left) or an
    /// expired lease whose attempts are exhausted (terminal cleanup). Shared by the claim and the elastic backlog count.
    /// </summary>
    private const string ReadinessWhere = """
                AND ((job.[GraphExecutionId] IS NULL OR EXISTS (
                        SELECT 1
                        FROM [CentralProcessingGraphExecutions] AS execution
                        WHERE execution.[Id] = job.[GraphExecutionId]
                          AND execution.[ExpandedAtUtc] IS NOT NULL
                          AND execution.[Status] = N'Running'))
                    AND (((job.[Status] IN (N'Pending', N'RetryableFailure')
                            AND job.[AttemptCount] < job.[MaxAttempts]
                            AND job.[InputSetIdentitySha256] IS NOT NULL
                            AND job.[AvailableAtUtc] <= @now)
                        OR (job.[Status] = N'Leased'
                            AND job.[LeaseExpiresAtUtc] <= @now
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
                        AND job.[LeaseExpiresAtUtc] <= @now
                        AND job.[AttemptCount] >= job.[MaxAttempts])))
        """;

    /// <summary>
    /// The claimable set exactly as the claim sees it (recipe filter, input-size bound, readiness), without ranking,
    /// fairness, or locks: one row per claimable runner-placed job with its observatory, recipe, whether it is terminal
    /// cleanup, and the age key. Used by the elastic autoscaler so it never provisions for work no runner could claim.
    /// </summary>
    internal static string CreateClaimableSql() => $"""
        SELECT job.[Id] AS [JobId], sourceFrame.[ObservatoryId] AS [ObservatoryId], job.[RecipeName] AS [RecipeName],
            CASE WHEN job.[Status] = N'Leased' AND job.[AttemptCount] >= job.[MaxAttempts] THEN CAST(1 AS bit) ELSE CAST(0 AS bit) END AS [IsCleanup],
            CASE WHEN job.[Status] = N'Leased' THEN job.[LeaseExpiresAtUtc] ELSE job.[AvailableAtUtc] END AS [AvailableSince],
            (SELECT COALESCE(SUM(sized.[ByteLength]), 0)
             FROM [CentralDerivativeJobInputs] AS sizedInput
             INNER JOIN [CentralArtifacts] AS sized ON sized.[Id] = sizedInput.[CentralArtifactId]
             WHERE sizedInput.[CentralDerivativeJobId] = job.[Id]) AS [InputBytes]
        FROM [CentralDerivativeJobs] AS job WITH (NOLOCK)
        INNER JOIN [CentralArtifacts] AS sourceArtifact ON sourceArtifact.[Id] = job.[SourceCentralArtifactId]
        INNER JOIN [CentralFrames] AS sourceFrame ON sourceFrame.[Id] = sourceArtifact.[CentralFrameId]
        WHERE
        {RecipeAndSizeWhere}
        {ReadinessWhere}
        """;

    internal static string CreateCandidateSql(bool fairness, bool idOnly = false)
    {
        // Active-lease aggregates are computed once per query (not once per candidate row) so the fairness cost is
        // linear in candidates plus active leases. The CTE requires the id-only form to run as a raw command. The
        // aggregates are a ranking heuristic re-validated under the entitlement lock, so they read dirty (NOLOCK):
        // a lease being written counts as active immediately and a lease being released stops counting immediately,
        // whereas skipping locked rows made every observatory look idle whenever its leases were in transition and
        // the order collapsed to plain availability order under concurrent short jobs.
        var fairPrefix = fairness
            ? """
              WITH active AS (
                  SELECT a.[Id], fa.[ObservatoryId] AS [o], fa.[DevicePublicId] AS [d], COALESCE(rc.[cls], N'image') AS [cls],
                      (SELECT COALESCE(SUM(sz.[ByteLength]), 0) FROM [CentralDerivativeJobInputs] AS i
                       INNER JOIN [CentralArtifacts] AS sz ON sz.[Id] = i.[CentralArtifactId]
                       WHERE i.[CentralDerivativeJobId] = a.[Id]) AS [bytes]
                  FROM [CentralDerivativeJobs] AS a WITH (NOLOCK)
                  INNER JOIN [CentralArtifacts] AS sa ON sa.[Id] = a.[SourceCentralArtifactId]
                  INNER JOIN [CentralFrames] AS fa ON fa.[Id] = sa.[CentralFrameId]
                  LEFT JOIN OPENJSON(@classes) WITH ([r] nvarchar(128) '$.r', [cls] nvarchar(64) '$.cls') AS rc ON rc.[r] = a.[RecipeName]
                  WHERE a.[Status] = N'Leased' AND a.[LeaseExpiresAtUtc] > @now),
              byObservatory AS (SELECT [o], COUNT(*) AS [n] FROM active GROUP BY [o]),
              byCamera AS (SELECT [o], [d], COUNT(*) AS [n] FROM active GROUP BY [o], [d]),
              byClass AS (SELECT [cls], COUNT(*) AS [n], SUM([bytes]) AS [b] FROM active GROUP BY [cls]),
              byObservatoryClass AS (SELECT [o], [cls], COUNT(*) AS [n] FROM active GROUP BY [o], [cls]),
              served AS (SELECT [ObservatoryId] AS [o], COUNT(*) AS [n] FROM [CentralProcessingUsageRecords] WITH (NOLOCK)
                         WHERE [RecordedAtUtc] > @servedSince GROUP BY [ObservatoryId])
              """
            : string.Empty;
        var fairJoins = fairness
            ? """
              INNER JOIN [CentralArtifacts] AS sourceArtifact ON sourceArtifact.[Id] = job.[SourceCentralArtifactId]
              INNER JOIN [CentralFrames] AS sourceFrame ON sourceFrame.[Id] = sourceArtifact.[CentralFrameId]
              LEFT JOIN OPENJSON(@entitlements) WITH ([o] uniqueidentifier '$.o', [a] int '$.a', [c] int '$.c', [w] float '$.w', [p] int '$.p', [pool] nvarchar(64) '$.pool') AS ent
                  ON ent.[o] = sourceFrame.[ObservatoryId]
              LEFT JOIN OPENJSON(@classes) WITH ([r] nvarchar(128) '$.r', [cls] nvarchar(64) '$.cls', [a] int '$.a', [b] bigint '$.b') AS rc
                  ON rc.[r] = job.[RecipeName]
              LEFT JOIN byObservatory AS bo ON bo.[o] = sourceFrame.[ObservatoryId]
              LEFT JOIN byCamera AS bc ON bc.[o] = sourceFrame.[ObservatoryId] AND bc.[d] = sourceFrame.[DevicePublicId]
              LEFT JOIN byClass AS bcl ON bcl.[cls] = COALESCE(rc.[cls], N'image')
              LEFT JOIN byObservatoryClass AS boc ON boc.[o] = sourceFrame.[ObservatoryId] AND boc.[cls] = COALESCE(rc.[cls], N'image')
              LEFT JOIN served AS sv ON sv.[o] = sourceFrame.[ObservatoryId]
              CROSS APPLY (SELECT
                  COALESCE(bo.[n], 0) AS [ObservatoryActive],
                  COALESCE(sv.[n], 0) AS [ObservatoryServed],
                  COALESCE(bc.[n], 0) AS [CameraActive],
                  COALESCE(bcl.[n], 0) AS [ClassActive],
                  COALESCE(bcl.[b], 0) AS [ClassActiveBytes],
                  COALESCE(boc.[n], 0) AS [ObservatoryClassActive],
                  (SELECT TOP(1) ocl.[a] FROM OPENJSON(@classLimits) WITH ([o] uniqueidentifier '$.o', [cls] nvarchar(64) '$.cls', [a] int '$.a') AS ocl
                   WHERE ocl.[o] = sourceFrame.[ObservatoryId] AND ocl.[cls] = COALESCE(rc.[cls], N'image')) AS [ObservatoryClassLimit],
                  (SELECT COALESCE(SUM(cb.[ByteLength]), 0) FROM [CentralDerivativeJobInputs] AS ci
                   INNER JOIN [CentralArtifacts] AS cb ON cb.[Id] = ci.[CentralArtifactId]
                   WHERE ci.[CentralDerivativeJobId] = job.[Id]) AS [CandidateBytes]) AS fair
              """
            : string.Empty;
        // An expired lease with exhausted attempts is terminal cleanup, not new work: it is exempt from every
        // entitlement, exclusion, and pool predicate so it can never be stranded in Leased behind a saturated quota.
        var fairWhere = fairness
            ? """
              AND ((job.[Status] = N'Leased' AND job.[LeaseExpiresAtUtc] <= @now AND job.[AttemptCount] >= job.[MaxAttempts])
                  OR ((COALESCE(ent.[a], @defaultActive) = 0 OR fair.[ObservatoryActive] < COALESCE(ent.[a], @defaultActive))
                      AND (COALESCE(ent.[c], @defaultCamera) = 0 OR fair.[CameraActive] < COALESCE(ent.[c], @defaultCamera))
                      AND (COALESCE(rc.[a], 0) = 0 OR fair.[ClassActive] < rc.[a])
                      AND (COALESCE(rc.[b], 0) = 0 OR fair.[ClassActiveBytes] + fair.[CandidateBytes] <= rc.[b])
                      AND (fair.[ObservatoryClassLimit] IS NULL OR fair.[ObservatoryClassLimit] = 0 OR fair.[ObservatoryClassActive] < fair.[ObservatoryClassLimit])
                      AND sourceFrame.[ObservatoryId] NOT IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@excluded))
                      AND sourceFrame.[DevicePublicId] NOT IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@excludedCameras))
                      AND COALESCE(rc.[cls], N'image') NOT IN (SELECT [value] FROM OPENJSON(@excludedClasses))
                      AND NOT EXISTS (SELECT 1 FROM OPENJSON(@excludedObservatoryClasses) WITH ([o] uniqueidentifier '$.o', [cls] nvarchar(64) '$.cls') AS excludedPair
                                      WHERE excludedPair.[o] = sourceFrame.[ObservatoryId] AND excludedPair.[cls] = COALESCE(rc.[cls], N'image'))
                      AND ((@poolMode = 0 AND ent.[pool] IS NULL)
                          OR (@poolMode = 1 AND (ent.[pool] IS NULL OR ent.[pool] = @pool))
                          OR (@poolMode = 2 AND ent.[pool] = @pool))))
              AND job.[Id] NOT IN (SELECT CAST([value] AS uniqueidentifier) FROM OPENJSON(@excludedJobs))
              """
            : string.Empty;
        // Ranking keys: starvation, pool affinity, priority, weighted share, then the original availability order.
        string[] fairKeys =
        [
            "CASE WHEN job.[Status] <> N'Leased' AND job.[AvailableAtUtc] <= @starvationBefore THEN 0 ELSE 1 END",
            "CASE WHEN @poolMode = 1 AND ent.[pool] = @pool THEN 0 ELSE 1 END",
            "COALESCE(ent.[p], @defaultPriority)",
            "(fair.[ObservatoryActive] + fair.[ObservatoryServed] + 1.0) / COALESCE(ent.[w], @defaultWeight)"
        ];
        string[] ageKeys =
        [
            "CASE WHEN job.[Status] = N'Leased' THEN job.[LeaseExpiresAtUtc] ELSE job.[AvailableAtUtc] END",
            "job.[CreatedAtUtc]",
            "job.[Id]"
        ];
        var fairOrder = fairness ? string.Join(",\n              ", fairKeys) + "," : string.Empty;
        // The id-only batch is ordered breadth-first: within equal fair keys, one job per observatory precedes a second
        // job of any observatory (ROW_NUMBER per observatory), so concurrent claimers spread across observatories
        // instead of all holding the same observatory's jobs.
        var rankedColumns = string.Join(", ", fairKeys.Select((key, index) => $"{key} AS [k{index}]")
            .Concat(ageKeys.Select((key, index) => $"{key} AS [a{index}]")));
        var rowNumber = $"ROW_NUMBER() OVER (PARTITION BY sourceFrame.[ObservatoryId] ORDER BY {string.Join(", ", fairKeys.Concat(ageKeys))}) AS [rn]";
        var selectList = idOnly ? $"job.[Id] AS [Value], {rankedColumns}, {rowNumber}" : "job.*";
        var outerOrder = string.Join(", ", fairKeys.Select((_, index) => $"ranked.[k{index}]")
            .Append("ranked.[rn]")
            .Concat(ageKeys.Select((_, index) => $"ranked.[a{index}]")));
        var top = idOnly ? FairCandidateBatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture) : "1";
        // Ranking a few hundred rows does not benefit from a parallel plan, and many concurrent claimers each taking
        // every scheduler thread oversubscribe the database host; the locked forms are composed by EF Core and
        // cannot carry a query hint.
        var queryHint = idOnly ? "OPTION (MAXDOP 1)" : string.Empty;
        // The id-only form ranks candidates without taking locks and skips rows another claimer is updating; the locked
        // form re-validates the single chosen row. Both avoid waiting behind in-flight lease transactions.
        var lockHints = idOnly ? "WITH (READPAST)" : "WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK, ROWLOCK)";
        var open = idOnly ? $"SELECT TOP({top}) ranked.[Value] FROM (SELECT {selectList}" : $"SELECT TOP({top}) {selectList}";
        var close = idOnly ? $") AS ranked ORDER BY {outerOrder}" : string.Empty;
        return $"""
            {fairPrefix}
            {open}
            FROM [CentralDerivativeJobs] AS job {lockHints}
            {fairJoins}
            WHERE
            {RecipeAndSizeWhere}
                {fairWhere}
            {ReadinessWhere}
            {(idOnly ? close : $"ORDER BY {fairOrder} {string.Join(", ", ageKeys)}")}
            {queryHint}
            """;
    }

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
