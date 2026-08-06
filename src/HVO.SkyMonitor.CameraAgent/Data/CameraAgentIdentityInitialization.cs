namespace HVO.SkyMonitor.CameraAgent.Data;

internal sealed class CameraAgentIdentityInitialization
{
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal bool IsCompleted => completion.Task.IsCompletedSuccessfully;

    internal Task WaitAsync(CancellationToken cancellationToken)
        => completion.Task.WaitAsync(cancellationToken);

    internal async Task RunAsync(
        Func<CancellationToken, Task> initialize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initialize);
        try
        {
            await initialize(cancellationToken).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
            throw;
        }
    }
}
