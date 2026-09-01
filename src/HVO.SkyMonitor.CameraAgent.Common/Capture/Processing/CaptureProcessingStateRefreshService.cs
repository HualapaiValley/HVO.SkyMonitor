using Microsoft.Extensions.Hosting;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CaptureProcessingStateRefreshService(
    SqliteCaptureProcessingStore store,
    CaptureProcessingState state,
    TimeProvider timeProvider,
    IRawCaptureIngress? rawIngress = null) : BackgroundService
{
    internal async ValueTask RunOnceAsync(CancellationToken cancellationToken)
    {
        if (rawIngress is not null)
        {
            await rawIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        var durable = await store.ReadOperationalStateAsync(cancellationToken).ConfigureAwait(false);
        var inventory = await store.ReadAvailabilityInventoryAsync(cancellationToken).ConfigureAwait(false);
        state.SetDurable(
            durable.PendingCount,
            durable.RetryCount,
            durable.TerminalCount,
            durable.OldestPendingUtc);
        state.SetReplayDurable(
            durable.ReplayPendingCount,
            durable.ReplayRetryCount,
            durable.ReplayTerminalCount,
            durable.OldestReplayPendingUtc,
            durable.ReplayPendingBytes);
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
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                state.SetRefreshFailure();
            }

            await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken).ConfigureAwait(false);
        }
    }
}
