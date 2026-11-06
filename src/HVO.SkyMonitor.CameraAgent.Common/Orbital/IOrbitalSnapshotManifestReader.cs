using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>
/// Reads only the small, verified manifest of a retained orbital snapshot: its coverage, counts, epoch range and source
/// provenance. It never reads or materializes the normalized payload, so an operator page can describe the installed
/// data without the cost of <see cref="IOrbitalSnapshotReader"/>.
/// </summary>
public interface IOrbitalSnapshotManifestReader
{
    /// <summary>
    /// Returns the manifest whose stored bytes prove <paramref name="manifestSha256"/>, or null when the identity is
    /// malformed (checked before any I/O), unknown or retired.
    /// </summary>
    /// <exception cref="OrbitalStorageException">
    /// The store is unavailable, or the stored manifest is missing, damaged or does not prove its identity. The
    /// exception carries a stable reason code and never a path.
    /// </exception>
    /// <exception cref="OperationCanceledException">The caller's token was cancelled.</exception>
    ValueTask<OrbitalSnapshotManifestV1?> ReadManifestAsync(string manifestSha256, CancellationToken cancellationToken);
}
