using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record EnvironmentalRetentionSnapshot(
    DateTimeOffset? LastSucceededUtc,
    string? FailureReason,
    bool TerminalFailure);

internal sealed class EnvironmentalRetentionState
{
    private EnvironmentalRetentionSnapshot _snapshot = new(null, null, false);
    public EnvironmentalRetentionSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public void Succeeded(DateTimeOffset now) => Volatile.Write(ref _snapshot, new(now, null, false));
    public void Failed(Exception exception) => Volatile.Write(
        ref _snapshot,
        new(Snapshot.LastSucceededUtc, exception.GetType().Name, exception is InvalidOperationException or InvalidDataException));
}

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The worker records health and continues after transient infrastructure faults.")]
internal sealed class EnvironmentalObservationRetentionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<EnvironmentalObservationOptions> options,
    TimeProvider timeProvider,
    EnvironmentalRetentionState state,
    EnvironmentalObservationTelemetry telemetry,
    ILogger<EnvironmentalObservationRetentionWorker> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, Exception?> BatchDeleted = LoggerMessage.Define<int>(
        LogLevel.Information,
        new EventId(2510, nameof(BatchDeleted)),
        "Environmental observation retention deleted {Rows} bounded rows");
    private static readonly Action<ILogger, Exception?> SweepFailed = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(2511, nameof(SweepFailed)),
        "Environmental observation retention sweep failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
                state.Succeeded(timeProvider.GetUtcNow());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                state.Failed(exception);
                SweepFailed(logger, exception);
            }
            await Task.Delay(TimeSpan.FromSeconds(options.Value.RetentionSweepSeconds), timeProvider, stoppingToken)
                .ConfigureAwait(false);
        }
    }

    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = EnvironmentalObservationTelemetry.ActivitySource.StartActivity("environment.retention");
        try
        {
            var deleted = 0;
            while (true)
            {
                var batch = await SweepBatchAsync(cancellationToken).ConfigureAwait(false);
                deleted += batch;
                if (batch == 0)
                {
                    break;
                }
            }
            var duration = timeProvider.GetElapsedTime(started);
            telemetry.RecordRetention(deleted, duration, "succeeded");
            activity?.SetTag("environment.deleted_rows", deleted);
            activity?.SetTag("environment.outcome", "succeeded");
            return deleted;
        }
        catch (Exception exception)
        {
            telemetry.RecordRetention(0, timeProvider.GetElapsedTime(started), "failed");
            activity?.SetTag("environment.outcome", "failed");
            activity?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, exception.GetType().Name);
            throw;
        }
    }

    internal async Task<int> SweepBatchAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var receiptCutoff = now - TimeSpan.FromDays(options.Value.ReceiptRetentionDays);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        if (transaction is not null)
        {
            var lockResource = EnvironmentalObservationLockNames.Retention;
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock
                    @Resource = {lockResource},
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 10000;
                IF @result < 0
                    THROW 51007, 'Could not acquire the environmental retention lock.', 1;
                """, cancellationToken).ConfigureAwait(false);
        }
        var ids = await dbContext.EnvironmentalObservations
            .Where(observation => observation.ReceivedAtUtc < receiptCutoff &&
                observation.ValidThroughUtc < now && !observation.ReferencedBy.Any() &&
                !dbContext.CentralDerivativeJobCanonicalInputs.Any(input =>
                    input.EnvironmentalObservationRecordId == observation.Id))
            .OrderBy(observation => observation.ReceivedAtUtc)
            .ThenBy(observation => observation.ValidThroughUtc)
            .ThenBy(observation => observation.Id)
            .Select(observation => observation.Id)
            .Take(options.Value.RetentionBatchSize)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        var batch = ids.Length == 0
            ? 0
            : await dbContext.EnvironmentalObservations
                .Where(observation => ids.Contains(observation.Id) && !observation.ReferencedBy.Any() &&
                    !dbContext.CentralDerivativeJobCanonicalInputs.Any(input =>
                        input.EnvironmentalObservationRecordId == observation.Id))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        if (batch > 0)
        {
            BatchDeleted(logger, batch, null);
        }
        return batch;
    }
}
