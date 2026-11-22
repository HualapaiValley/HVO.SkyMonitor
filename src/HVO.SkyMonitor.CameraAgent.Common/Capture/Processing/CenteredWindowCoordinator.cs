using System.Threading.Channels;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

// Closing on a deadline is independent of both acquisition and registration:
// a stalled camera or a slow image worker cannot keep the slot calendar open.
internal sealed partial class CenteredWindowCoordinator(
    SqliteCaptureProcessingStore store,
    ICenteredWindowJournal journal,
    TimeProvider timeProvider,
    ILogger<CenteredWindowCoordinator> logger,
    RawCaptureIngress? ingress = null,
    CenteredWindowWakeup? wakeup = null) : BackgroundService
{
    internal const string RegistrationProducer = "centered-live-registration-v1";

    private const int CloseBatchSize = 16;
    private static readonly TimeSpan TimerResolution = TimeSpan.FromSeconds(1);
    private CenteredWindowQueueEntry? _lastChecked;
    private readonly Channel<string> _evidence = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });

    // The journal calls this after the corresponding slot/product commit. A
    // lost wakeup is recovered by the durable deadline queue.
    internal void NotifyEvidenceCommitted(string windowIdentity) => _evidence.Writer.TryWrite(windowIdentity);

    internal async ValueTask CloseDueAsync(CancellationToken cancellationToken)
    {
        var due = await store.ReadDueCenteredWindowsAsync(_lastChecked, CloseBatchSize, cancellationToken).ConfigureAwait(false);
        foreach (var entry in due)
            await CloseAsync(entry.WindowIdentity, cancellationToken).ConfigureAwait(false);
        // Advance past a failed row so another due window can close, then wrap to retry it.
        _lastChecked = due.Count == CloseBatchSize ? due[^1] : null;
    }

    private async ValueTask CloseAsync(string identity, CancellationToken cancellationToken)
    {
        try
        {
            await store.TryCloseCenteredWindowAsync(identity, journal, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            CloseFailed(logger, identity, exception);
        }
    }

    internal async ValueTask CloseReadyAsync(CancellationToken cancellationToken)
    {
        var pending = await store.ReadOpenCenteredWindowIdsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var identity in pending)
            await CloseAsync(identity, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteRuntimeAsync(CancellationToken stoppingToken)
    {
        var evidenceReady = false;
        var observed = -1L;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!evidenceReady)
                {
                    await ingress!.WaitForCenteredWindowEvidenceAsync(stoppingToken).ConfigureAwait(false);
                    evidenceReady = true;
                }
                var version = wakeup!.Version;
                var recovered = await store.RecoverUnobservedCenteredEvidenceAsync(stoppingToken).ConfigureAwait(false);
                if (version != observed || recovered)
                {
                    await CloseReadyAsync(stoppingToken).ConfigureAwait(false);
                    observed = version;
                }
                await CloseDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Retry the sweep after a failed read or recovery even if the wakeup version is unchanged.
                observed = -1;
                ReadDueFailed(logger, exception);
                await Task.Delay(TimerResolution, timeProvider, stoppingToken).ConfigureAwait(false);
                continue;
            }

            using var interval = new CancellationTokenSource(TimerResolution, timeProvider);
            using var tick = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, interval.Token);
            try
            {
                await wakeup!.WaitForChangeAsync(observed, tick.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (interval.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (ingress is not null && wakeup is not null)
        {
            await ExecuteRuntimeAsync(stoppingToken).ConfigureAwait(false);
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CloseDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ReadDueFailed(logger, exception);
            }

            using var interval = new CancellationTokenSource(TimerResolution, timeProvider);
            using var tick = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, interval.Token);
            try
            {
                while (await _evidence.Reader.WaitToReadAsync(tick.Token).ConfigureAwait(false) &&
                    _evidence.Reader.TryRead(out var identity))
                {
                    await CloseAsync(identity, stoppingToken).ConfigureAwait(false);
                    if (interval.IsCancellationRequested)
                        break;
                }
            }
            catch (OperationCanceledException) when (interval.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
            }
        }
    }

    [LoggerMessage(EventId = 6190, Level = LogLevel.Error, Message = "The centered window coordinator could not read the deadline queue.")]
    private static partial void ReadDueFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6191, Level = LogLevel.Error, Message = "Centered window {WindowIdentity} could not complete its scheduled close.")]
    private static partial void CloseFailed(ILogger logger, string windowIdentity, Exception exception);
}
