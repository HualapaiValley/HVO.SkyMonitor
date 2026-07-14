using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

internal sealed record FrameProcessingItem(
    CameraModuleConfig Config,
    CaptureLoopSubmission Submission,
    RawCaptureReceipt? RawCapture = null);

internal sealed class FrameProcessingChannel
{
    private readonly Channel<FrameProcessingItem> _channel;
    private long _acceptedCount;
    private long _dequeuedCount;

    public FrameProcessingChannel(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(capacity, 1);

        var options = new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        };

        _channel = Channel.CreateBounded<FrameProcessingItem>(options);
        Capacity = capacity;
    }

    public int Capacity { get; }

    public int CurrentDepth => _channel.Reader.Count;

    public long AcceptedCount => Interlocked.Read(ref _acceptedCount);

    public long DequeuedCount => Interlocked.Read(ref _dequeuedCount);

    public async ValueTask WriteAsync(FrameProcessingItem item, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _acceptedCount);
    }

    public bool TryWrite(FrameProcessingItem item)
    {
        if (!_channel.Writer.TryWrite(item))
        {
            return false;
        }
        Interlocked.Increment(ref _acceptedCount);
        return true;
    }

    public async IAsyncEnumerable<FrameProcessingItem> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _dequeuedCount);
            yield return item;
        }
    }

    public void Complete(Exception? exception = null)
        => _channel.Writer.TryComplete(exception);
}
