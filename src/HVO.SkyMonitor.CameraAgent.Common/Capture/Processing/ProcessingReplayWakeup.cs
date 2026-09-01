using System.Threading.Channels;
using System.Collections.Concurrent;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class ProcessingReplayWakeup
{
    private readonly Channel<bool> _channel;
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _livePreemptions = new();
    private long _nextPreemptionId;

    public ProcessingReplayWakeup(IOptions<CameraAgentHostOptions> options)
    {
        _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(
            options.Value.ProcessingGraphs.ReplayMaximumConcurrency)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false
        });
    }

    internal void Signal() => _channel.Writer.TryWrite(true);

    internal IDisposable RegisterLivePreemption(CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        var id = Interlocked.Increment(ref _nextPreemptionId);
        if (!_livePreemptions.TryAdd(id, cancellation))
            throw new InvalidOperationException("The replay live-preemption registration could not be created.");
        return new LivePreemptionRegistration(_livePreemptions, id);
    }

    internal void SignalLiveWork()
    {
        foreach (var cancellation in _livePreemptions.Values)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal async ValueTask WaitAsync(TimeSpan recoveryPoll, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var timer = new CancellationTokenSource(recoveryPoll, timeProvider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        try
        {
            _ = await _channel.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private sealed class LivePreemptionRegistration(
        ConcurrentDictionary<long, CancellationTokenSource> registrations,
        long id) : IDisposable
    {
        private ConcurrentDictionary<long, CancellationTokenSource>? _registrations = registrations;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _registrations, null);
            _ = current?.TryRemove(id, out _);
        }
    }
}
