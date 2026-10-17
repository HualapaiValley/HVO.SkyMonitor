namespace HVO.SkyMonitor.CameraAgent.Common.Capture;

/// <summary>
/// Holds retention and cleanup of bulk capture data while capture admission is not running.
/// </summary>
/// <remarks>
/// An installer upgrade or rollback pauses admission durably before it stops the previous image, and resumes it only
/// after the candidate commits or the previous image is restored. A candidate therefore starts with the hold already in
/// force and cannot delete frames, archives or derived products the previous image still needs while the swap is
/// undecided; the hold survives a crash or power loss because the pause it reads is durable. An operator pause holds
/// them too, and a paused instance captures nothing new, so the hold cannot grow the data it protects.
/// </remarks>
internal static class CaptureMaintenanceHold
{
    /// <summary>The admission state that holds maintenance deletions, or <see langword="null"/> when they may run.</summary>
    public static async ValueTask<CaptureAdmissionState?> HeldByAsync(
        CaptureAdmissionCoordinator? admission,
        CancellationToken cancellationToken)
    {
        if (admission is null)
        {
            return null;
        }
        await admission.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = admission.Snapshot;
        return snapshot is { IsInitialized: true, State: CaptureAdmissionState.Running } ? null : snapshot.State;
    }
}
