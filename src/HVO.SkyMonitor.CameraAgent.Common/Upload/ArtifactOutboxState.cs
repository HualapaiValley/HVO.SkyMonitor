using System.Collections.Concurrent;

namespace HVO.SkyMonitor.CameraAgent.Common.Upload;

public enum ArtifactOutboxAvailability
{
    Initializing,
    Healthy,
    Degraded,
    Unavailable
}

public sealed record ArtifactOutboxStateSnapshot(
    ArtifactOutboxAvailability Availability,
    long PendingCount,
    long PendingBytes,
    long LeasedCount,
    long RetryCount,
    long QuarantineCount,
    DateTimeOffset? OldestPendingUtc,
    string? FailureReason);

public sealed class ArtifactOutboxState
{
    private readonly ConcurrentDictionary<string, ArtifactOutboxSnapshot> _roots = new(PathComparer);
    private readonly ConcurrentDictionary<string, string> _failures = new(PathComparer);
    private int _initialized;

    public ArtifactOutboxStateSnapshot Snapshot
    {
        get
        {
            var snapshots = _roots.Values.ToArray();
            var failure = _failures.Values.Order(StringComparer.Ordinal).FirstOrDefault();
            var retry = snapshots.Sum(static item => item.RetryCount);
            var quarantine = snapshots.Sum(static item => item.QuarantinedCount);
            var availability = failure is not null
                ? ArtifactOutboxAvailability.Unavailable
                : snapshots.Length == 0 && Volatile.Read(ref _initialized) == 0
                    ? ArtifactOutboxAvailability.Initializing
                    : retry > 0 || quarantine > 0
                        ? ArtifactOutboxAvailability.Degraded
                        : ArtifactOutboxAvailability.Healthy;
            return new ArtifactOutboxStateSnapshot(
                availability,
                snapshots.Sum(static item => item.HeldCount),
                snapshots.Sum(static item => item.HeldBytes),
                snapshots.Sum(static item => item.LeasedCount),
                retry,
                quarantine,
                snapshots.Where(static item => item.OldestHeldUtc.HasValue)
                    .Select(static item => item.OldestHeldUtc)
                    .DefaultIfEmpty()
                    .Min(),
                failure);
        }
    }

    public void Update(string root, ArtifactOutboxSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(snapshot);
        _roots[Path.GetFullPath(root)] = snapshot;
        Volatile.Write(ref _initialized, 1);
        _failures.TryRemove(Path.GetFullPath(root), out _);
    }

    public void MarkInitialized() => Volatile.Write(ref _initialized, 1);

    public void ReportUnavailable(string root, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _failures[Path.GetFullPath(root)] = reason.Length <= 256 ? reason : reason[..256];
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
