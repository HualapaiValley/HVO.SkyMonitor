using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Minio.Exceptions;
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
    ILogger<CentralDerivativeWorker> logger) : BackgroundService
{
    private readonly CentralDerivativeWorkerOptions _options = options.Value;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            Log.Lifecycle(logger, "disabled", _options.Concurrency);
            return Task.CompletedTask;
        }
        Log.Lifecycle(logger, "started", _options.Concurrency);
        return Task.WhenAll(Enumerable.Range(0, _options.Concurrency)
            .Select(slot => RunSlotAsync(slot, stoppingToken)));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A durable worker slot must retry after transient claim failures.")]
    private async Task RunSlotAsync(int slot, CancellationToken stoppingToken)
    {
        var nextQueueSampleUtc = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            telemetry.RecordPoll(timeProvider.GetUtcNow());
            CentralDerivativeJobLease? lease;
            var claimStarted = timeProvider.GetTimestamp();
            try
            {
                await using var claimScope = scopeFactory.CreateAsyncScope();
                if (slot == 0)
                {
                    if (claimScope.ServiceProvider.GetService<ICentralTransientRetrospectiveScheduler>() is { } scheduler)
                    {
                        await scheduler.ScheduleBatchAsync(timeProvider.GetUtcNow(), stoppingToken)
                            .ConfigureAwait(false);
                    }
                    await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeWindowResolver>()
                        .ResolveWaitingAsync(timeProvider.GetUtcNow(), stoppingToken)
                        .ConfigureAwait(false);
                }
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
                nextQueueSampleUtc = await SampleQueueIfDueAsync(
                    slot, nextQueueSampleUtc, stoppingToken).ConfigureAwait(false);
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
            nextQueueSampleUtc = await SampleQueueIfDueAsync(
                slot, nextQueueSampleUtc, stoppingToken).ConfigureAwait(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A hosted worker must persist terminal failure and continue processing later durable jobs.")]
    private async Task ExecuteLeaseAsync(CentralDerivativeJobLease lease, CancellationToken stoppingToken)
    {
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        try
        {
            await using var executionScope = scopeFactory.CreateAsyncScope();
            using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
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
            if (executionFailure is not null)
            {
                if (renewalFailure is not null)
                {
                    throw new CentralDerivativeJobStateException(
                        "Central derivative lease renewal failed; local execution was canceled.",
                        renewalFailure);
                }
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
        catch (MinioException exception)
        {
            telemetry.RecordAttempt(lease.RecipeName, "retryable", "storage", timeProvider.GetUtcNow());
            telemetry.RecordDependencyFailure("storage", timeProvider.GetUtcNow());
            await TryFailAsync(lease, exception.GetType().Name, retryable: true, stoppingToken).ConfigureAwait(false);
            Log.Outcome(logger, lease.JobId, lease.AttemptCount, lease.WorkerId, lease.RecipeName,
                "RetryableFailure", "storage.unavailable");
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

    private async Task<DateTimeOffset> SampleQueueIfDueAsync(
        int slot,
        DateTimeOffset nextQueueSampleUtc,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (slot != 0 || now < nextQueueSampleUtc)
        {
            return nextQueueSampleUtc;
        }
        await SampleQueueAsync(now, cancellationToken).ConfigureAwait(false);
        return now + _options.QueueSampleInterval;
    }

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

        [LoggerMessage(2139, LogLevel.Warning,
            "Central derivative failure persistence failed: JobId={JobId}, Attempt={Attempt}")]
        public static partial void FailurePersistenceFailed(
            ILogger logger, Exception exception, Guid jobId, int attempt);
    }
}
