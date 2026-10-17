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
/// <para>
/// A pass that is admitted holds a maintenance lease for everything it might delete. The pause waits for every such
/// lease before it is acknowledged and grants none after its gate closes, so no deletion follows an acknowledged pause.
/// </para>
/// </remarks>
internal static class CaptureMaintenanceHold
{
    /// <summary>
    /// Enters one destructive maintenance pass. The result is held when admission is not running; otherwise it carries
    /// the lease the pass keeps until its last deletion. Without an admission coordinator nothing is held.
    /// </summary>
    public static async ValueTask<CaptureMaintenanceEntry> EnterAsync(
        CaptureAdmissionCoordinator? admission,
        CancellationToken cancellationToken)
    {
        if (admission is null)
        {
            return default;
        }
        var lease = await admission.TryEnterMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        if (lease is not null)
        {
            return new CaptureMaintenanceEntry(lease, null);
        }
        var snapshot = admission.Snapshot;
        return new CaptureMaintenanceEntry(
            null, snapshot.IsInitialized ? snapshot.State : CaptureAdmissionState.Unavailable);
    }
}

/// <summary>Whether a maintenance pass may delete, and the lease it holds while it does.</summary>
internal readonly record struct CaptureMaintenanceEntry(
    CaptureAdmissionCoordinator.CaptureMaintenanceLease? Lease,
    CaptureAdmissionState? HeldBy) : IDisposable
{
    public bool Held => HeldBy is not null;

    public void Dispose() => Lease?.Dispose();
}
