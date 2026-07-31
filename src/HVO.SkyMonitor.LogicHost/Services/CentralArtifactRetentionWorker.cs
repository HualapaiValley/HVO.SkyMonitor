using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed partial class CentralArtifactRetentionWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<CentralArtifactRetentionOptions> options,
    CentralArtifactRetentionTelemetry telemetry,
    ILogger<CentralArtifactRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                LogRecovery("failed", "worker", "reconcile", 0, 0, 0);
            }
            await Task.Delay(options.Value.PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task<int> ProcessDueAsync(CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        using var activity = CentralArtifactRetentionTelemetry.Start("central-artifact.retention.reconcile");
        await using var queryScope = scopeFactory.CreateAsyncScope();
        var db = queryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = timeProvider.GetUtcNow();
        var ids = await db.CentralObjectRecoveryDispositions.AsNoTracking()
            .Where(item => item.Kind == CentralObjectRecoveryKinds.ExpiredDelete
                && item.OperationToken != null
                && item.State == CentralObjectRecoveryStates.PendingDelete
                && (item.NextAttemptAtUtc == null || item.NextAttemptAtUtc <= now))
            .OrderBy(item => item.NextAttemptAtUtc ?? item.UpdatedAtUtc)
            .ThenBy(item => item.Id)
            .Take(options.Value.BatchSize)
            .Select(item => item.Id)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var outcomes = new Dictionary<string, (int Count, long Bytes)>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionProcessor>()
                    .ProcessAsync(id, "worker", cancellationToken).ConfigureAwait(false);
                var state = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                    .CentralObjectRecoveryDispositions.AsNoTracking()
                    .Where(item => item.Id == id)
                    .Select(item => new { item.State, item.ByteLength })
                    .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (state is null)
                {
                    continue;
                }
                var outcome = result == CentralArtifactRetentionProcessResult.Released
                    ? "deleted"
                    : state.State == CentralObjectRecoveryStates.Failed ? "terminal" : "retry";
                AddOutcome(outcomes, outcome, state.ByteLength);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                var bytes = await ScheduleItemRetryAsync(id).ConfigureAwait(false);
                AddOutcome(outcomes, exception is InvalidOperationException ? "conflict" : "retry", bytes);
            }
        }
        var elapsed = timeProvider.GetElapsedTime(started);
        foreach (var (outcome, value) in outcomes)
        {
            telemetry.RecordRecovery(outcome, value.Count, elapsed);
        }
        await RecordBacklogAsync(db, now, cancellationToken).ConfigureAwait(false);
        if (ids.Length != 0)
        {
            var outcome = outcomes.Count == 1 ? outcomes.Keys.Single() : "mixed";
            LogRecovery(
                outcome,
                "worker",
                "reconcile",
                outcomes.Values.Sum(item => item.Count),
                outcomes.Values.Sum(item => item.Bytes),
                elapsed.TotalMilliseconds);
            activity?.SetTag("retention.outcome", outcome);
        }
        else
        {
            activity?.SetTag("retention.outcome", "drained");
        }
        return ids.Length;
    }

    private async Task<long> ScheduleItemRetryAsync(Guid dispositionId)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var current = await db.CentralObjectRecoveryDispositions.AsNoTracking()
                .Where(item => item.Id == dispositionId
                    && item.OperationToken != null
                    && item.State == CentralObjectRecoveryStates.PendingDelete)
                .Select(item => new { item.ByteLength, item.OperationToken, item.RowVersion })
                .SingleOrDefaultAsync(CancellationToken.None).ConfigureAwait(false);
            if (current is null)
            {
                return 0;
            }
            var now = timeProvider.GetUtcNow();
            _ = await db.CentralObjectRecoveryDispositions.Where(item =>
                    item.Id == dispositionId
                    && item.OperationToken == current.OperationToken
                    && item.State == CentralObjectRecoveryStates.PendingDelete
                    && item.RowVersion == current.RowVersion)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.NextAttemptAtUtc, now + TimeSpan.FromSeconds(1))
                    .SetProperty(item => item.UpdatedAtUtc, now), CancellationToken.None)
                .ConfigureAwait(false);
            return current.ByteLength;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return 0;
        }
    }

    private static void AddOutcome(
        Dictionary<string, (int Count, long Bytes)> outcomes,
        string outcome,
        long bytes)
    {
        var current = outcomes.GetValueOrDefault(outcome);
        outcomes[outcome] = (current.Count + 1, current.Bytes + bytes);
    }

    private async Task RecordBacklogAsync(
        ApplicationDbContext db,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var backlog = await (from disposition in db.CentralObjectRecoveryDispositions.AsNoTracking()
                             join artifact in db.CentralArtifacts.AsNoTracking()
                                 on disposition.CentralArtifactId equals (Guid?)artifact.Id
                             where disposition.Kind == CentralObjectRecoveryKinds.ExpiredDelete
                                 && disposition.OperationToken != null
                                 && disposition.State == CentralObjectRecoveryStates.PendingDelete
                                 && artifact.RetentionDeletionToken == disposition.OperationToken
                                 && artifact.RetentionDeletionRequestedAtUtc != null
                             select new
                             {
                                 disposition.ByteLength,
                                 RequestedAtUtc = artifact.RetentionDeletionRequestedAtUtc!.Value
                             })
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.LongCount(),
                Bytes = group.Sum(item => item.ByteLength),
                Oldest = group.Min(item => item.RequestedAtUtc)
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        telemetry.RecordBacklog(
            backlog?.Count ?? 0,
            backlog?.Bytes ?? 0,
            backlog is null ? 0 : Math.Max(0, (long)(now - backlog.Oldest).TotalSeconds));
    }

    [LoggerMessage(2173, LogLevel.Information,
        "Central artifact retention recovery: Outcome={Outcome} Origin={Origin} Stage={Stage} Count={Count} Bytes={Bytes} DurationMs={DurationMs}")]
    private partial void LogRecovery(
        string outcome, string origin, string stage, int count, long bytes, double durationMs);

    private static bool IsRecoverable(Exception exception)
        => exception is Microsoft.Data.SqlClient.SqlException
            or Microsoft.EntityFrameworkCore.DbUpdateException
            or Minio.Exceptions.MinioException
            or HttpRequestException
            or IOException
            or TimeoutException
            or OperationCanceledException
            or InvalidOperationException;
}
