namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>A coalescing, process-local wakeup after a centered product transaction commits.</summary>
/// <remarks>Use IExposureProductReader.ReadProductsAfterAsync for durable order and recovery.</remarks>
internal sealed class CenteredExposureProductChanges
{
    private readonly Lock _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _version;

    internal long Version => Interlocked.Read(ref _version);

    internal Task WaitForChangeAsync(long observedVersion, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return observedVersion == _version ? _changed.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
        }
    }

    internal void NotifyCommitted()
    {
        TaskCompletionSource ready;
        lock (_gate)
        {
            ready = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref _version);
        }
        ready.TrySetResult();
    }
}
