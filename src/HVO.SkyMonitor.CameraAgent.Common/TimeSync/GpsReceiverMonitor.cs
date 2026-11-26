using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeSync;

/// <summary>Single owner of the configured serial descriptor. Configuration changes and disconnects start a new evidence epoch.</summary>
internal sealed class GpsReceiverMonitor(
    Func<GpsReceiverSettings?> settings,
    Func<GpsReceiverSettings, IGpsSerialDevice> open,
    TimeProvider time) : BackgroundService
{
    // This release supports receiver dates in a window narrower than a single 1024-week GPS rollover.
    // Outside that deployment era a subsequent release must supply a new window. Never infer it from host UTC.
    internal static GpsUtcWindow ReceiverEra { get; } = new(new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2035, 12, 31, 23, 59, 59, TimeSpan.Zero));
    private volatile Session? _session;

    internal GpsTimeStatus? Status(GpsReceiverSettings configured)
    {
        if (!configured.Enabled) return null;
        var current = _session;
        return current?.Settings == configured ? current.Reader.GetStatus() : GpsTimeStatus.Empty(GpsSignalState.Unavailable);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var configured = settings();
            if (configured is { Enabled: true } && configured.Validate() is null)
            {
                try
                {
                    using var device = open(configured);
                    using var reader = new GpsTimeReader(time, ReceiverEra);
                    _session = new(configured, reader);
                    await PollAsync(device, reader, configured, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
                {
                    // An unavailable device is retried. No evidence survives a disconnect or a new descriptor.
                }
                finally
                {
                    _session = null;
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(1), time, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PollAsync(IGpsSerialDevice device, GpsTimeReader reader, GpsReceiverSettings configured, CancellationToken token)
    {
        var buffer = new byte[512];
        long? lastLow = null;
        var pollInterval = TimeSpan.FromMilliseconds(configured.UsePps ? 10 : 40);
        // Re-read configuration once per second, independent of serial traffic.
        var checkedAt = time.GetTimestamp();
        while (!token.IsCancellationRequested)
        {
            if (time.GetElapsedTime(checkedAt) >= TimeSpan.FromSeconds(1))
            {
                if (configured != settings()) return;
                checkedAt = time.GetTimestamp();
            }
            var before = time.GetTimestamp();
            var carrier = configured.UsePps ? device.CarrierDetect() : null;
            var after = time.GetTimestamp();
            var count = device.Read(buffer);
            if (carrier == false)
            {
                lastLow = before;
            }
            else if (carrier == true && lastLow.HasValue)
            {
                // If bytes are already queued when high is first seen, their order relative to the pulse is unknown.
                if (count == 0) reader.ObserveRisingEdge(lastLow.Value, after);
                else reader.LosePps();
                lastLow = null;
            }
            else if (configured.UsePps && !carrier.HasValue)
            {
                lastLow = null;
                reader.LosePps();
            }
            reader.ConsumeSerialBytes(buffer.AsSpan(0, count));
            await Task.Delay(pollInterval, time, token).ConfigureAwait(false);
        }
    }

    private sealed record Session(GpsReceiverSettings Settings, GpsTimeReader Reader);
}
