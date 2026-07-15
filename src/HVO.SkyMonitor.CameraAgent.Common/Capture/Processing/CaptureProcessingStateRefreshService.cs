using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CaptureProcessingStateRefreshService(
    SqliteCaptureProcessingStore store,
    CaptureProcessingState state,
    TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var durable = await store.ReadOperationalStateAsync(stoppingToken).ConfigureAwait(false);
                state.SetDurable(
                    durable.PendingCount,
                    durable.RetryCount,
                    durable.TerminalCount,
                    durable.OldestPendingUtc);
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
