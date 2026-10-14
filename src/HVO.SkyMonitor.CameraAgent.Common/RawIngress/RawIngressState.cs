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

internal sealed record RawIngressBaseSnapshot(
    RawIngressAvailability Availability,
    string Reason,
    long PendingCount,
    long PendingBytes,
    long QuarantineCount,
    long QuarantineBytes,
    DateTimeOffset? OldestPendingUtc);

/// <summary>
/// The counts from the most recent completed raw-ingress reconciliation, which runs once when ingress initializes.
/// It carries counts only, never a path or an evidence key.
/// </summary>
public sealed record RawIngressReconciliationReport(
    DateTimeOffset CompletedUtc,
    int Inspected,
    int Recovered,
    int Cleaned,
    int Quarantined,
    int MissingEvidence,
    int IndexProjectionFailures);

public sealed class RawIngressState(TimeProvider timeProvider)
{
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly object _gate = new();
    private RawIngressAvailability _baseAvailability = RawIngressAvailability.Initializing;
    private string _baseReason = "initializing";
    private long _pendingCount;
    private long _pendingBytes;
    private long _quarantineCount;
    private long _quarantineBytes;
    private DateTimeOffset? _oldestPendingUtc;
    private int _projectedSceneBacklog;
    private RawIngressSnapshot _snapshot = new(
        RawIngressAvailability.Initializing,
        "initializing",
        0,
        0,
        0,
        0,
        null,
        timeProvider.GetUtcNow());

    private RawIngressReconciliationReport? _lastReconciliation;

    public RawIngressSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public RawIngressReconciliationReport? LastReconciliation => Volatile.Read(ref _lastReconciliation);

    internal void RecordReconciliation(RawIngressReconciliationSummary summary) => Volatile.Write(
        ref _lastReconciliation,
        new RawIngressReconciliationReport(
            _timeProvider.GetUtcNow(), summary.Inspected, summary.Recovered, summary.Cleaned,
            summary.Quarantined, summary.MissingEvidence, summary.IndexProjectionFailures));

    internal void Set(
        RawIngressAvailability availability,
        string reason,
        long pendingCount = 0,
        long pendingBytes = 0,
        long quarantineCount = 0,
        long quarantineBytes = 0,
        DateTimeOffset? oldestPendingUtc = null)
    {
        lock (_gate)
        {
            _baseAvailability = availability;
            _baseReason = reason;
            _pendingCount = pendingCount;
            _pendingBytes = pendingBytes;
            _quarantineCount = quarantineCount;
            _quarantineBytes = quarantineBytes;
            _oldestPendingUtc = oldestPendingUtc;
            PublishSnapshot();
        }
    }

    internal DateTimeOffset GetUtcNow() => _timeProvider.GetUtcNow();

    internal RawIngressBaseSnapshot GetBaseSnapshot()
    {
        lock (_gate)
        {
            return new RawIngressBaseSnapshot(
                _baseAvailability, _baseReason, _pendingCount, _pendingBytes,
                _quarantineCount, _quarantineBytes, _oldestPendingUtc);
        }
    }

    internal void UpdateBaseStatus(RawIngressAvailability availability, string reason)
    {
        lock (_gate)
        {
            _baseAvailability = availability;
            _baseReason = reason;
            PublishSnapshot();
        }
    }

    internal void UpdateBaseHeldData(long pendingCount, long pendingBytes, DateTimeOffset? oldestPendingUtc)
    {
        lock (_gate)
        {
            _pendingCount = pendingCount;
            _pendingBytes = pendingBytes;
            _oldestPendingUtc = oldestPendingUtc;
            PublishSnapshot();
        }
    }

    internal void SetProjectedSceneBacklog(int backlog)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(backlog);
        lock (_gate)
        {
            _projectedSceneBacklog = backlog;
            PublishSnapshot();
        }
    }

    private void PublishSnapshot()
    {
        var backlogOnlyDegradation = _projectedSceneBacklog > 0 &&
            _baseAvailability == RawIngressAvailability.Accepting;
        Volatile.Write(ref _snapshot, new RawIngressSnapshot(
            backlogOnlyDegradation ? RawIngressAvailability.Degraded : _baseAvailability,
            backlogOnlyDegradation ? "projected-scene-backlog" : _baseReason,
            _pendingCount,
            _pendingBytes,
            _quarantineCount,
            _quarantineBytes,
            _oldestPendingUtc,
            _timeProvider.GetUtcNow()));
    }
}
