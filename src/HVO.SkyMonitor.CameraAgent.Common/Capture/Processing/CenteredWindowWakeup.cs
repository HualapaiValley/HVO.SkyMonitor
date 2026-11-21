namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

// This signal includes capture and run commits as well as product commits.
internal sealed class CenteredWindowWakeup
{
    private readonly CenteredExposureProductChanges _changes = new();

    internal long Version => _changes.Version;

    internal Task WaitForChangeAsync(long version, CancellationToken cancellationToken) =>
        _changes.WaitForChangeAsync(version, cancellationToken);

    internal void NotifyCommitted() => _changes.NotifyCommitted();
}
