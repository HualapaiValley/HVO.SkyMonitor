namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

public enum RawIngressAvailability
{
    Initializing,
    Accepting,
    Degraded,
    Unhealthy
}

public sealed record RawIngressSnapshot(
    RawIngressAvailability Availability,
    string Reason,
    long PendingCount,
    long PendingBytes,
    long QuarantineCount,
    long QuarantineBytes,
    DateTimeOffset? OldestPendingUtc,
    DateTimeOffset EvaluatedUtc);

public sealed class RawIngressState(TimeProvider timeProvider)
{
    private readonly TimeProvider _timeProvider = timeProvider;
    private RawIngressSnapshot _snapshot = new(
        RawIngressAvailability.Initializing,
        "initializing",
        0,
        0,
        0,
        0,
        null,
        timeProvider.GetUtcNow());

    public RawIngressSnapshot Snapshot => Volatile.Read(ref _snapshot);

    internal void Set(
        RawIngressAvailability availability,
        string reason,
        long pendingCount = 0,
        long pendingBytes = 0,
        long quarantineCount = 0,
        long quarantineBytes = 0,
        DateTimeOffset? oldestPendingUtc = null)
        => Volatile.Write(ref _snapshot, new RawIngressSnapshot(
            availability,
            reason,
            pendingCount,
            pendingBytes,
            quarantineCount,
            quarantineBytes,
            oldestPendingUtc,
            _timeProvider.GetUtcNow()));

    internal DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();
}
