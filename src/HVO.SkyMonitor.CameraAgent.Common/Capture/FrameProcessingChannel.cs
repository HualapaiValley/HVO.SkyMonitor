using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

internal sealed record FrameProcessingItem(CameraModuleConfig Config, CaptureLoopSubmission Submission);

internal sealed class FrameProcessingChannel
{
    private readonly Channel<FrameProcessingItem> _channel;

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
    }

    public ValueTask WriteAsync(FrameProcessingItem item, CancellationToken cancellationToken)
        => _channel.Writer.WriteAsync(item, cancellationToken);

    public IAsyncEnumerable<FrameProcessingItem> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete(Exception? exception = null)
        => _channel.Writer.TryComplete(exception);
}
