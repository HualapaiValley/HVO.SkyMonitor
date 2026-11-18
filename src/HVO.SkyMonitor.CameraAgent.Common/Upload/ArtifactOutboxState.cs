using System.Collections.Concurrent;
using HVO.SkyMonitor.CameraAgent.Common.Storage;

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
    string? FailureReason,
    DateTimeOffset? EvaluatedUtc = null);

public sealed class ArtifactOutboxState
{
    private readonly ConcurrentDictionary<string, RootState> _roots = new(PathComparer);
    private readonly ConcurrentDictionary<string, string> _failures = new(PathComparer);
    private int _initialized;
    private long _evaluatedUnixMilliseconds = long.MinValue;

    public ArtifactOutboxStateSnapshot Snapshot
    {
        get
        {
            var snapshots = _roots.Values.Select(static root => root.Snapshot).ToArray();
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
                snapshots.Select(static item => item.OldestHeldUtc)
                    .Where(static timestamp => timestamp.HasValue)
                    .Min(),
                failure,
                Latest(
                    snapshots.Select(static item => item.EvaluatedUtc),
                    Volatile.Read(ref _evaluatedUnixMilliseconds)));
        }
    }

    private sealed record RootState(string Path, ArtifactOutboxSnapshot Snapshot);

    public IReadOnlyList<string> Roots => _roots.Values.Select(static root => root.Path).ToArray();

    public void Update(string root, ArtifactOutboxSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(snapshot);
        var key = StorageRootIdentity.Key(root);
        var path = StorageRootIdentity.CanonicalPath(root);
        _roots.AddOrUpdate(key, new RootState(path, snapshot),
            (_, existing) => existing with { Snapshot = snapshot });
        MarkEvaluated();
        Volatile.Write(ref _initialized, 1);
        _failures.TryRemove(key, out _);
    }

    public void MarkInitialized()
    {
        MarkEvaluated();
        Volatile.Write(ref _initialized, 1);
    }

    public void ReportUnavailable(string root, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _failures[StorageRootIdentity.Key(root)] = reason.Length <= 256 ? reason : reason[..256];
        MarkEvaluated();
    }

    private void MarkEvaluated()
        => Interlocked.Exchange(ref _evaluatedUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static DateTimeOffset? Latest(IEnumerable<DateTimeOffset?> snapshots, long stateTimestamp)
    {
        var timestamps = stateTimestamp == long.MinValue
            ? snapshots
            : snapshots.Append(DateTimeOffset.FromUnixTimeMilliseconds(stateTimestamp));
        return timestamps.Max();
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
