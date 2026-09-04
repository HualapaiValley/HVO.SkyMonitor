using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Data.Common;
using System.Runtime.ExceptionServices;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralDerivativeWorkerOptions
{
    public const string SectionName = "CentralDerivativeWorker";

    public bool Enabled { get; init; } = true;

    public string WorkerId { get; init; } = "logic-host-derivative-worker";

    public int Concurrency { get; init; } = 1;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan QueueSampleInterval { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan RenewalInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan BacklogDegradedAfter { get; init; } = TimeSpan.FromMinutes(10);
}

internal sealed partial class CentralDerivativeWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<CentralDerivativeWorkerOptions> options,
    CentralDerivativeWorkerTelemetry telemetry,
    TimeProvider timeProvider,
    ILogger<CentralDerivativeWorker> logger,
    CentralProcessingGraphConvergenceSignal? graphConvergenceSignal = null,
    CentralProcessingFairnessTelemetry? fairnessTelemetry = null,
    IOptions<CentralProcessingEntitlementOptions>? entitlementOptions = null) : BackgroundService
{
    private const int MaximumSignaledConvergencesPerPass = 64;
    private readonly CentralDerivativeWorkerOptions _options = options.Value;
    private readonly CentralProcessingGraphConvergenceSignal _graphConvergenceSignal =
        graphConvergenceSignal ?? new CentralProcessingGraphConvergenceSignal();

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            Log.Lifecycle(logger, "disabled", _options.Concurrency);
            return Task.CompletedTask;
        }
        Log.Lifecycle(logger, "started", _options.Concurrency);
        return Task.WhenAll(Enumerable.Range(0, _options.Concurrency)
            .Select(slot => RunSlotAsync(slot, stoppingToken))
            .Append(RunMaintenanceAsync(stoppingToken)));
    }

    /// <summary>
    /// Every periodic duty of the worker runs here, on its own loop, so a slot executing a long recipe never delays
    /// it: signaled and periodic graph convergence, retrospective transient scheduling, waiting-window resolution,
    /// and queue sampling. The health check's <c>graph-recovery-stale</c> and <c>window-overdue</c> signals therefore
    /// only fire when maintenance genuinely stops. Each duty is serialized by its own database locks, so running
    /// them while any slot executes a lease is safe, and the signal channel keeps its single reader here. Window
    /// resolution used to run immediately before each slot-0 claim; at <see cref="CentralDerivativeWorkerOptions.PollInterval"/>
    /// cadence a newly resolvable window waits at most one interval, the same bound the idle claim loop already had.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A durable maintenance loop must retry after transient database failures.")]
    private async Task RunMaintenanceAsync(CancellationToken stoppingToken)
    {
        var nextGraphRecoveryUtc = DateTimeOffset.MinValue;
        var nextQueueSampleUtc = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var faulted = false;
            try
            {
                // Resolved inside the guarded loop: a dependency that fails to construct (object storage credentials,
                // for example) is logged and retried rather than faulting this task silently for the process lifetime.
                await using var scope = scopeFactory.CreateAsyncScope();
                // Each duty is guarded on its own so one duty's activation or database fault never starves the others:
                // waiting windows must still resolve while, say, graph convergence cannot construct its object reader.
                faulted |= !await RunDutyAsync(() => ConvergeGraphsAsync(scope.ServiceProvider, stoppingToken), stoppingToken)
                    .ConfigureAwait(false);
                faulted |= !await RunDutyAsync(async () =>
                {
                    if (scope.ServiceProvider.GetService<ICentralTransientRetrospectiveScheduler>() is { } retrospective)
                    {
                        await retrospective.ScheduleBatchAsync(timeProvider.GetUtcNow(), stoppingToken)
                            .ConfigureAwait(false);
                    }
                }, stoppingToken).ConfigureAwait(false);
                faulted |= !await RunDutyAsync(() => scope.ServiceProvider
                    .GetRequiredService<ICentralDerivativeWindowResolver>()
                    .ResolveWaitingAsync(timeProvider.GetUtcNow(), stoppingToken), stoppingToken).ConfigureAwait(false);
                var sampleAt = timeProvider.GetUtcNow();
                if (sampleAt >= nextQueueSampleUtc)
                {
                    await SampleQueueAsync(sampleAt, stoppingToken).ConfigureAwait(false);
                    nextQueueSampleUtc = sampleAt + _options.QueueSampleInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Scope construction itself failed; the per-duty guards cover everything inside the scope.
                Log.MaintenanceFailed(logger, exception);
                faulted = true;
            }
            if (faulted)
            {
                // The signal wait below returns immediately while ids remain queued, so back off unconditionally
                // after a fault instead of spinning through the queue against an unavailable database.
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }
            await _graphConvergenceSignal.WaitAsync(_options.PollInterval, stoppingToken).ConfigureAwait(false);
        }

        async Task ConvergeGraphsAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            if (services.GetService<ICentralProcessingGraphScheduler>() is not { } graphScheduler)
            {
                return;
            }
            // Drain a bounded batch per pass so a sustained signal stream cannot starve the other duties; anything
            // left in the channel makes the wait below return immediately and is drained on the next pass.
            var signaledAt = timeProvider.GetUtcNow();
            for (var drained = 0; drained < MaximumSignaledConvergencesPerPass &&
                 _graphConvergenceSignal.TryRead(out var graphExecutionId); drained++)
            {
                await ConvergeSignaledAsync(graphScheduler, graphExecutionId, signaledAt, cancellationToken)
                    .ConfigureAwait(false);
            }
            // Read the clock after the drain: the batch records its poll instant as LastGraphRecoveryUtc, and a
            // long signal burst must not make a recovery that just completed look stale.
            var recoveryAt = timeProvider.GetUtcNow();
            if (recoveryAt >= nextGraphRecoveryUtc)
            {
                await graphScheduler.ConvergeBatchAsync(recoveryAt, cancellationToken).ConfigureAwait(false);
                nextGraphRecoveryUtc = recoveryAt + _options.QueueSampleInterval;
            }
        }
    }

    /// <summary>
    /// Runs one maintenance duty; returns false when it faulted. Only real database faults degrade database health;
    /// an activation failure or a duty's own state fault is logged and retried without a misleading dependency label.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A faulted duty must not stop the other maintenance duties or the loop.")]
    private async Task<bool> RunDutyAsync(Func<Task> duty, CancellationToken stoppingToken)
    {
        try
        {
            await duty().ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (CentralProcessingGraphScheduler.IsDatabaseFailure(exception))
            {
                telemetry.RecordDependencyFailure("database", timeProvider.GetUtcNow());
            }
            Log.MaintenanceFailed(logger, exception);
            return false;
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A durable worker slot must retry after transient claim failures.")]
    private async Task RunSlotAsync(int slot, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            telemetry.RecordPoll(timeProvider.GetUtcNow());
            CentralDerivativeJobLease? lease;
            var claimStarted = timeProvider.GetTimestamp();
            try
            {
                await using var claimScope = scopeFactory.CreateAsyncScope();
                using (telemetry.StartStage("claim", "other"))
                {
                    lease = await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                        .ClaimNextAsync(_options.WorkerId, _options.LeaseDuration, stoppingToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                telemetry.RecordClaim("failed", timeProvider.GetElapsedTime(claimStarted));
                telemetry.RecordDependencyFailure("database", timeProvider.GetUtcNow());
                Log.ClaimFailed(logger, exception, slot);
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }
            telemetry.RecordClaim(
                lease is null ? "empty" : "claimed",
                timeProvider.GetElapsedTime(claimStarted));
            if (lease is null)
            {
                await Task.Delay(_options.PollInterval, stoppingToken).ConfigureAwait(false);
                continue;
            }
            Log.Attempt(logger, lease.JobId, lease.AttemptCount, lease.RecipeName, slot);
            using (telemetry.TrackActive())
            using (telemetry.StartExecution(
                lease.RecipeName, lease.TraceParent, lease.TraceState, lease.JobId, lease.AttemptCount))
            {
                await ExecuteLeaseAsync(lease, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Direct-signal convergence applies the same failure classification as batch convergence
    /// (<see cref="CentralProcessingGraphScheduler.ConvergeBatchAsync"/>): only real database faults degrade database
    /// health, while a graph whose frozen state cannot converge is logged here. The convergence "failed" outcome
    /// itself is emitted exactly once by <see cref="CentralProcessingGraphScheduler.ConvergeAsync"/> for every thrown
    /// convergence (batch or signaled), so this method must not record a second one. Database faults propagate so
    /// the maintenance loop records the dependency failure and backs off as before.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A corrupt signaled execution must not be reported as a database outage or stop the claim loop.")]
    private async Task ConvergeSignaledAsync(
        ICentralProcessingGraphScheduler graphScheduler,
        Guid graphExecutionId,
        DateTimeOffset now,
        CancellationToken stoppingToken)
    {
        try
        {
            await graphScheduler.ConvergeAsync(graphExecutionId, now, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!CentralProcessingGraphScheduler.IsDatabaseFailure(exception))
        {
            Log.SignaledConvergenceFailed(logger, exception, graphExecutionId);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A hosted worker must persist terminal failure and continue processing later durable jobs.")]
    private async Task ExecuteLeaseAsync(CentralDerivativeJobLease lease, CancellationToken stoppingToken)
    {
        var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            try
            {
                await using var executionScope = scopeFactory.CreateAsyncScope();
                var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                try
                {
                    var execution = executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                        .ExecuteAsync(lease, executionCancellation.Token);
                    var renewal = RenewUntilCanceledAsync(lease, executionCancellation, renewalCancellation.Token);
                    CentralDerivativeExecutionResult? result = null;
                    Exception? executionFailure = null;
                    try
                    {
                        result = await execution.ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        executionFailure = exception;
                    }
                    finally
                    {
                        await renewalCancellation.CancelAsync().ConfigureAwait(false);
                    }
                    var renewalFailure = await renewal.ConfigureAwait(false);
                    if (renewalFailure is CentralDerivativeLeaseCanceledException)
                    {
                        // Cancellation reached this leased node: renewal was refused, the linked execution token was
                        // canceled, and the lease now runs out so graph convergence records Canceled through the
                        // expiry path. The job row stays untouched here; CentralDerivativeJob remains the authority.
                        telemetry.RecordAttempt(
                            lease.RecipeName, "canceled", "cancellation", timeProvider.GetUtcNow());
                        Log.Outcome(logger, lease.JobId, lease.AttemptCount, lease.WorkerId, lease.RecipeName,
                            "Canceled", "processing.graph.cancel-requested");
                        return;
                    }
                    if (renewalFailure is not null)
                    {
                        throw new CentralDerivativeJobStateException(
                            "Central derivative lease renewal failed; local execution was canceled.",
                            renewalFailure);
                    }
                    if (executionFailure is not null)
                    {
                        ExceptionDispatchInfo.Capture(executionFailure).Throw();
                    }
                    telemetry.RecordAttempt(
                        lease.RecipeName,
                        GetOutcome(result!.Status),
                        result.ReasonCode is null ? "none" : "recipe",
                        timeProvider.GetUtcNow());
                    Log.Outcome(logger, lease.JobId, lease.AttemptCount, lease.WorkerId, lease.RecipeName,
                        result.Status.ToString(), result.ReasonCode);
                }
                finally
                {
                    renewalCancellation.Dispose();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown leaves the lease for expiry and safe reclamation.
            }
            catch (CentralDerivativeInputRejectedException exception)
            {
                telemetry.RecordAttempt(lease.RecipeName, "terminal", "input", timeProvider.GetUtcNow());
                await TryFailAsync(lease, exception.Message, retryable: false, stoppingToken).ConfigureAwait(false);
                Log.Outcome(logger, lease.JobId, lease.AttemptCount, lease.WorkerId, lease.RecipeName,
                    "TerminalFailure", "input.rejected");
            }
            catch (CentralDerivativeJobStateException exception)
            {
                telemetry.RecordAttempt(lease.RecipeName, "lease-lost", "lease", timeProvider.GetUtcNow());
                Log.LeaseLost(logger, exception, lease.JobId, lease.AttemptCount);
            }
            catch (CentralArtifactMissingException exception)
            {
                telemetry.RecordAttempt(lease.RecipeName, "retryable", "source-missing", timeProvider.GetUtcNow());
                Log.Outcome(logger, lease.JobId, lease.AttemptCount, lease.WorkerId, lease.RecipeName,
                    "RetryableFailure", exception.Message);
            }
            catch (CentralArtifactStorageException exception)
            {
                telemetry.RecordAttempt(lease.RecipeName, "retryable", "storage", timeProvider.GetUtcNow());
                telemetry.RecordDependencyFailure("storage", timeProvider.GetUtcNow());
                await TryFailAsync(lease, exception.Message, retryable: true, stoppingToken).ConfigureAwait(false);
                Log.Outcome(logger, lease.JobId, lease.AttemptCount, lease.WorkerId, lease.RecipeName,
                    "RetryableFailure", "storage.unavailable");
            }
            catch (ObjectStoreException exception)
            {
                var retryable = !exception.IsTerminal;
                var outcome = retryable ? "retryable" : "terminal";
                telemetry.RecordAttempt(lease.RecipeName, outcome, "storage", timeProvider.GetUtcNow());
                telemetry.RecordDependencyFailure("storage", timeProvider.GetUtcNow());
                await TryFailAsync(lease, exception.GetType().Name, retryable, stoppingToken).ConfigureAwait(false);
                Log.Outcome(logger, lease.JobId, lease.AttemptCount, lease.WorkerId, lease.RecipeName,
                    retryable ? "RetryableFailure" : "TerminalFailure",
                    retryable ? "storage.unavailable" : "storage.terminal");
            }
            catch (CentralArtifactIntegrityException exception)
            {
                telemetry.RecordAttempt(lease.RecipeName, "quarantined", "source-integrity", timeProvider.GetUtcNow());
                Log.Outcome(logger, lease.JobId, lease.AttemptCount, lease.WorkerId, lease.RecipeName,
                    "Quarantined", exception.ReasonCode);
            }
            catch (CentralDerivativeOutputIntegrityException exception)
            {
                telemetry.RecordAttempt(lease.RecipeName, "quarantined", "output-integrity", timeProvider.GetUtcNow());
                Log.Outcome(logger, lease.JobId, lease.AttemptCount, lease.WorkerId, lease.RecipeName,
                    "Quarantined", exception.ReasonCode);
            }
            catch (DbUpdateException exception)
            {
                telemetry.RecordAttempt(lease.RecipeName, "retryable", "database", timeProvider.GetUtcNow());
                telemetry.RecordDependencyFailure("database", timeProvider.GetUtcNow());
                await TryFailAsync(lease, exception.GetType().Name, retryable: true, stoppingToken).ConfigureAwait(false);
                Log.Outcome(logger, lease.JobId, lease.AttemptCount, lease.WorkerId, lease.RecipeName,
                    "RetryableFailure", "database.unavailable");
            }
            catch (DbException exception)
            {
                telemetry.RecordAttempt(lease.RecipeName, "retryable", "database", timeProvider.GetUtcNow());
                telemetry.RecordDependencyFailure("database", timeProvider.GetUtcNow());
                await TryFailAsync(lease, exception.GetType().Name, retryable: true, stoppingToken).ConfigureAwait(false);
                Log.Outcome(logger, lease.JobId, lease.AttemptCount, lease.WorkerId, lease.RecipeName,
                    "RetryableFailure", "database.unavailable");
            }
            catch (Exception exception)
            {
                var reasonCode = exception is CentralTransientPersistenceException persistenceException
                    ? persistenceException.ReasonCode
                    : $"processing.execution-failed.{exception.GetType().Name}";
                telemetry.RecordAttempt(lease.RecipeName, "terminal", "execution", timeProvider.GetUtcNow());
                Log.Unexpected(logger, exception, lease.JobId, lease.AttemptCount);
                await TryFailAsync(lease, reasonCode, retryable: false, stoppingToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            executionCancellation.Dispose();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Any renewal failure means ownership is uncertain and local work must stop.")]
    private async Task<Exception?> RenewUntilCanceledAsync(
        CentralDerivativeJobLease lease,
        CancellationTokenSource executionCancellation,
        CancellationToken renewalCancellation)
    {
        try
        {
            while (!renewalCancellation.IsCancellationRequested)
            {
                await Task.Delay(_options.RenewalInterval, renewalCancellation).ConfigureAwait(false);
                await using var scope = scopeFactory.CreateAsyncScope();
                _ = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                    .RenewLeaseAsync(lease.JobId, lease.LeaseToken, _options.LeaseDuration, renewalCancellation)
                    .ConfigureAwait(false);
                telemetry.RecordRenewal("renewed", timeProvider.GetUtcNow());
                telemetry.RecordPoll(timeProvider.GetUtcNow());
            }
        }
        catch (OperationCanceledException) when (renewalCancellation.IsCancellationRequested)
        {
            return null;
        }
        catch (CentralDerivativeLeaseCanceledException exception)
        {
            // Not a dependency failure: the lease authority refused renewal because cancellation was requested.
            telemetry.RecordRenewal("canceled", timeProvider.GetUtcNow());
            await executionCancellation.CancelAsync().ConfigureAwait(false);
            return exception;
        }
        catch (Exception exception)
        {
            telemetry.RecordRenewal("failed", timeProvider.GetUtcNow());
            telemetry.RecordDependencyFailure("database", timeProvider.GetUtcNow());
            await executionCancellation.CancelAsync().ConfigureAwait(false);
            return exception;
        }
        return null;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Best-effort failure persistence must not terminate a durable worker slot.")]
    private async Task TryFailAsync(
        CentralDerivativeJobLease lease,
        string error,
        bool retryable,
        CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .FailAsync(lease.JobId, lease.LeaseToken, error, retryable, stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (CentralDerivativeJobStateException exception)
        {
            Log.LeaseLost(logger, exception, lease.JobId, lease.AttemptCount);
        }
        catch (Exception exception)
        {
            telemetry.RecordDependencyFailure("database", timeProvider.GetUtcNow());
            Log.FailurePersistenceFailed(logger, exception, lease.JobId, lease.AttemptCount);
        }
    }

    /// <summary>
    /// Feeds the completion and byte counters from committed usage rows: rows are taken by marking them signaled in
    /// the same statement (so replicas never replay each other's rows) inside a transaction that commits only after
    /// the counters were emitted, so a process that stops in between leaves the rows unsignaled for the next pass
    /// (at-least-once emission; the usage table stays the durable source of truth). The safety-net sweep first
    /// records any terminal attempt of the last <see cref="UsageSweepWindow"/> that still lacks a usage row.
    /// </summary>
    private async Task SampleCommittedUsageAsync(
        ApplicationDbContext dbContext,
        CentralProcessingEntitlementOptions? entitlements,
        CancellationToken cancellationToken)
    {
        if (fairnessTelemetry is null)
        {
            return;
        }
        await CentralProcessingUsageRecorder.RecordMissingAsync(dbContext, entitlements, UsageSweepLimit, UsageSweepWindow, cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var signals = await CentralProcessingUsageRecorder.TakeUnsignaledAsync(dbContext, UsageSignalLimit, cancellationToken)
            .ConfigureAwait(false);
        foreach (var usage in signals.GroupBy(signal => (signal.ObservatoryId, signal.ResourceClass, signal.Outcome)))
        {
            fairnessTelemetry.RecordCommittedUsage(
                usage.Key.ObservatoryId, usage.Key.ResourceClass, usage.Key.Outcome.ToLowerInvariant(),
                usage.Count(), usage.Sum(signal => signal.InputBytes), usage.Sum(signal => signal.OutputBytes));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal const int UsageSweepLimit = 500;
    internal const int UsageSignalLimit = 5000;
    internal static readonly TimeSpan UsageSweepWindow = TimeSpan.FromHours(1);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Telemetry sampling must not interrupt durable derivative execution.")]
    private async Task SampleQueueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var statuses = new[]
            {
                CentralDerivativeJobStatus.Pending,
                CentralDerivativeJobStatus.RetryableFailure,
                CentralDerivativeJobStatus.Leased,
                CentralDerivativeJobStatus.Waiting
            };
            var snapshot = await dbContext.CentralDerivativeJobs.AsNoTracking()
                .Where(job => statuses.Contains(job.Status))
                .GroupBy(job => new { job.Status, job.RecipeName })
                .Select(group => new
                {
                    group.Key.Status,
                    group.Key.RecipeName,
                    Count = group.LongCount(),
                    Oldest = group.Min(job => job.AvailableAtUtc ?? job.CreatedAtUtc)
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var oldest = snapshot.Count == 0 ? 0 : (long)Math.Max(0, (now - snapshot.Min(item => item.Oldest)).TotalSeconds);
            if (fairnessTelemetry is not null)
            {
                var pendingStatuses = new[] { CentralDerivativeJobStatus.Pending, CentralDerivativeJobStatus.RetryableFailure };
                var observatories = await dbContext.CentralDerivativeJobs.AsNoTracking()
                    .Where(job => statuses.Contains(job.Status))
                    .GroupBy(job => job.SourceArtifact!.Frame!.ObservatoryId)
                    .Select(group => new
                    {
                        ObservatoryId = group.Key,
                        Pending = group.LongCount(job => pendingStatuses.Contains(job.Status)),
                        Leased = group.LongCount(job => job.Status == CentralDerivativeJobStatus.Leased && job.LeaseExpiresAtUtc > now),
                        Waiting = group.LongCount(job => job.Status == CentralDerivativeJobStatus.Waiting),
                        OldestPending = group.Where(job => pendingStatuses.Contains(job.Status))
                            .Min(job => (DateTimeOffset?)(job.AvailableAtUtc ?? job.CreatedAtUtc))
                    })
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                var entitlements = entitlementOptions?.Value;
                fairnessTelemetry.UpdateQueueSnapshot(observatories.Select(item => new CentralObservatoryQueueMeasurement(
                    item.ObservatoryId,
                    item.Pending,
                    item.Leased,
                    item.Waiting,
                    item.OldestPending is { } oldestPending ? (long)Math.Max(0, (now - oldestPending).TotalSeconds) : 0,
                    entitlements is { Enabled: true } ? entitlements.ResolveActiveJobs(item.ObservatoryId) : 0)).ToArray());
                await SampleCommittedUsageAsync(dbContext, entitlements, cancellationToken).ConfigureAwait(false);
            }
            telemetry.UpdateQueueSnapshot(
                snapshot.Select(item => new CentralDerivativeQueueMeasurement(
                    item.Status switch
                    {
                        CentralDerivativeJobStatus.Pending => "pending",
                        CentralDerivativeJobStatus.Leased => "leased",
                        CentralDerivativeJobStatus.RetryableFailure => "retryable",
                        CentralDerivativeJobStatus.Waiting => "waiting",
                        _ => "other"
                    },
                    item.RecipeName,
                    item.Count)).ToArray(),
                oldest);
            var activeStatuses = new[]
            {
                CentralDerivativeJobStatus.Waiting,
                CentralDerivativeJobStatus.Pending,
                CentralDerivativeJobStatus.Leased,
                CentralDerivativeJobStatus.RetryableFailure,
                CentralDerivativeJobStatus.CancelRequested
            };
            var window = await dbContext.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.Status == CentralDerivativeJobStatus.Waiting)
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    Count = group.LongCount(),
                    Oldest = group.Min(job => job.ResolutionStartedAtUtc ?? job.CreatedAtUtc)
                })
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var pins = await dbContext.CentralDerivativeJobInputs.AsNoTracking()
                .Where(input => activeStatuses.Contains(input.Job!.Status))
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    Count = group.LongCount(),
                    Bytes = group.Sum(input => input.ByteLength),
                    Oldest = group.Min(input => input.SelectedAtUtc)
                })
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            telemetry.UpdateWindowSnapshot(
                window?.Count ?? 0,
                window is null ? 0 : (long)Math.Max(0, (now - window.Oldest).TotalSeconds),
                pins?.Count ?? 0,
                pins is null ? 0 : (long)Math.Max(0, (now - pins.Oldest).TotalSeconds),
                pins?.Bytes ?? 0);
            var terminalGraphStatuses = new[]
            {
                CentralProcessingGraphExecutionStatus.Completed,
                CentralProcessingGraphExecutionStatus.CompletedWithOptionalFailures,
                CentralProcessingGraphExecutionStatus.Failed,
                CentralProcessingGraphExecutionStatus.Canceled,
                CentralProcessingGraphExecutionStatus.Superseded
            };
            var graphSnapshot = await dbContext.CentralProcessingGraphExecutions.AsNoTracking()
                .Where(execution => !terminalGraphStatuses.Contains(execution.Status))
                .GroupBy(execution => new { execution.ExecutionClass, execution.Status })
                .Select(group => new
                {
                    group.Key.ExecutionClass,
                    group.Key.Status,
                    Count = group.LongCount(),
                    Oldest = group.Min(execution => execution.UpdatedAtUtc)
                })
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            telemetry.UpdateGraphQueueSnapshot(
                graphSnapshot.Select(item => new CentralProcessingGraphQueueMeasurement(
                    item.ExecutionClass.ToString(), item.Status.ToString(), item.Count)).ToArray(),
                graphSnapshot.Length == 0
                    ? 0
                    : (long)Math.Max(0, (now - graphSnapshot.Min(item => item.Oldest)).TotalSeconds));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Database providers can surface command cancellation as their own exception type during shutdown.
        }
        catch (Exception exception)
        {
            telemetry.RecordDependencyFailure("database", timeProvider.GetUtcNow());
            Log.QueueSamplingFailed(logger, exception);
        }
    }

    private static string GetOutcome(HVO.SkyMonitor.Processing.ProcessingOutcomeStatus status) => status switch
    {
        HVO.SkyMonitor.Processing.ProcessingOutcomeStatus.Produced => "produced",
        HVO.SkyMonitor.Processing.ProcessingOutcomeStatus.Skipped => "skipped",
        HVO.SkyMonitor.Processing.ProcessingOutcomeStatus.RetryableFailure => "retryable",
        HVO.SkyMonitor.Processing.ProcessingOutcomeStatus.TerminalFailure => "terminal",
        _ => "other"
    };

    private static partial class Log
    {
        [LoggerMessage(2130, LogLevel.Information,
            "Central derivative worker {State}: Concurrency={Concurrency}")]
        public static partial void Lifecycle(ILogger logger, string state, int concurrency);

        [LoggerMessage(2131, LogLevel.Information,
            "Central derivative attempt claimed: JobId={JobId}, Attempt={Attempt}, Recipe={Recipe}, Slot={Slot}")]
        public static partial void Attempt(ILogger logger, Guid jobId, int attempt, string recipe, int slot);

        [LoggerMessage(2132, LogLevel.Information,
            "Central derivative attempt completed: JobId={JobId}, Attempt={Attempt}, Worker={Worker}, Recipe={Recipe}, Outcome={Outcome}, Reason={Reason}")]
        public static partial void Outcome(
            ILogger logger, Guid jobId, int attempt, string worker, string recipe, string outcome, string? reason);

        [LoggerMessage(2133, LogLevel.Warning,
            "Central derivative lease lost: JobId={JobId}, Attempt={Attempt}")]
        public static partial void LeaseLost(ILogger logger, Exception exception, Guid jobId, int attempt);

        [LoggerMessage(2136, LogLevel.Error,
            "Central derivative attempt failed unexpectedly: JobId={JobId}, Attempt={Attempt}")]
        public static partial void Unexpected(ILogger logger, Exception exception, Guid jobId, int attempt);

        [LoggerMessage(2137, LogLevel.Warning, "Unable to sample the central derivative queue for telemetry")]
        public static partial void QueueSamplingFailed(ILogger logger, Exception exception);

        [LoggerMessage(2138, LogLevel.Warning, "Central derivative claim failed: Slot={Slot}")]
        public static partial void ClaimFailed(ILogger logger, Exception exception, int slot);

        [LoggerMessage(2175, LogLevel.Error,
            "Central processing graph signaled convergence failed: ExecutionId={ExecutionId}")]
        public static partial void SignaledConvergenceFailed(ILogger logger, Exception exception, Guid executionId);

        [LoggerMessage(2176, LogLevel.Warning, "Central derivative worker maintenance loop failed; retrying")]
        public static partial void MaintenanceFailed(ILogger logger, Exception exception);

        [LoggerMessage(2139, LogLevel.Warning,
            "Central derivative failure persistence failed: JobId={JobId}, Attempt={Attempt}")]
        public static partial void FailurePersistenceFailed(
            ILogger logger, Exception exception, Guid jobId, int attempt);
    }
}
