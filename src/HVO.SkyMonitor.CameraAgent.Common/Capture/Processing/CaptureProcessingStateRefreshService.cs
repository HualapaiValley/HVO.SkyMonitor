using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CaptureProcessingStateRefreshService(
    SqliteCaptureProcessingStore store,
    CaptureProcessingState state,
    TimeProvider timeProvider) : BackgroundService
{
    internal async ValueTask RunOnceAsync(CancellationToken cancellationToken)
    {
        var durable = await store.ReadOperationalStateAsync(cancellationToken).ConfigureAwait(false);
        var inventory = await store.ReadAvailabilityInventoryAsync(cancellationToken).ConfigureAwait(false);
        state.SetDurable(
            durable.PendingCount,
            durable.RetryCount,
            durable.TerminalCount,
            durable.OldestPendingUtc);
        state.SetProcessingEvidence(inventory.MissingCount, inventory.QuarantinedCount);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // Raw ingress creates the shared lane tables during host startup.
                state.SetRefreshFailure();
            }

            await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}
