using System.Threading.Channels;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed class CentralProcessingGraphConvergenceSignal
{
    private readonly Channel<Guid> _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(1024)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    public void Signal(Guid? executionId)
    {
        if (executionId.HasValue)
        {
            _channel.Writer.TryWrite(executionId.Value);
        }
    }

    public bool TryRead(out Guid executionId) => _channel.Reader.TryRead(out executionId);

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            _ = await _channel.Reader.WaitToReadAsync(timeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}
