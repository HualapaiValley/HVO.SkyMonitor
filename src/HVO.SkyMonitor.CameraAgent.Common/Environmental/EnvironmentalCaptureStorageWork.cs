namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

internal static class EnvironmentalCaptureStorageWork
{
    // Microsoft.Data.Sqlite busy waits are synchronous even through its async API. A bounded hosted caller
    // awaits each operation on a dedicated thread, leaving the worker pool available for the admission deadline.
    internal static Task<T> RunAsync<T>(Func<ValueTask<T>> operation, CancellationToken cancellationToken)
        => Task.Factory.StartNew(() => operation().AsTask().GetAwaiter().GetResult(), cancellationToken,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);

    internal static Task RunAsync(Func<ValueTask> operation, CancellationToken cancellationToken)
        => Task.Factory.StartNew(() => operation().AsTask().GetAwaiter().GetResult(), cancellationToken,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
}
