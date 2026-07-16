using HVO.SkyMonitor.LogicHost.Configuration;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record FleetRetentionSnapshot(DateTimeOffset? LastSucceededUtc, string? FailureReason);

internal sealed class FleetRetentionState
{
    private FleetRetentionSnapshot _snapshot = new(null, null);
    public FleetRetentionSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public void Succeeded(DateTimeOffset now) => Volatile.Write(ref _snapshot, new(now, null));
    public void Failed(string reason) => Volatile.Write(ref _snapshot, new(Snapshot.LastSucceededUtc, reason));
}

[SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The retention worker records failure health and must continue after transient infrastructure faults.")]
internal sealed class FleetStatusRetentionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<FleetStatusOptions> options,
    TimeProvider timeProvider,
    FleetRetentionState state,
    FleetStatusTelemetry telemetry,
    ILogger<FleetStatusRetentionWorker> logger) : BackgroundService
{
    private const int BatchSize = 5_000;
    private static readonly Action<ILogger, int, Exception?> BatchDeleted = LoggerMessage.Define<int>(
        LogLevel.Information, new EventId(2410, nameof(BatchDeleted)),
        "Fleet heartbeat retention deleted {Rows} bounded history rows");
    private static readonly Action<ILogger, Exception?> SweepFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(2411, nameof(SweepFailed)),
        "Fleet heartbeat retention sweep failed");

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
                state.Failed(exception.GetType().Name);
                SweepFailed(logger, exception);
            }
            await Task.Delay(TimeSpan.FromHours(1), timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var receiptCutoff = now - TimeSpan.FromHours(options.Value.ReceiptRetentionHours);
        var snapshotCutoff = now - TimeSpan.FromDays(options.Value.SnapshotRetentionDays);
        var deleted = 0;
        while (true)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var ids = await dbContext.DeviceHeartbeatRecords
                .Where(record =>
                    (!record.IsSignificantSnapshot && record.ReceivedAtUtc < receiptCutoff) ||
                    (record.IsSignificantSnapshot && record.ReceivedAtUtc < snapshotCutoff))
                .OrderBy(record => record.ReceivedAtUtc)
                .ThenBy(record => record.Id)
                .Select(record => record.Id)
                .Take(BatchSize)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            if (ids.Length == 0)
            {
                break;
            }
            var batch = await dbContext.DeviceHeartbeatRecords
                .Where(record => ids.Contains(record.Id))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
            deleted += batch;
            telemetry.RecordRetention(batch);
            BatchDeleted(logger, batch, null);
            if (ids.Length < BatchSize)
            {
                break;
            }
        }
        return deleted;
    }
}
