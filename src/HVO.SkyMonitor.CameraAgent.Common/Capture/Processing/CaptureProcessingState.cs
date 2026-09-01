namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public enum CaptureProcessingAvailability { Healthy, Degraded, Unhealthy }

public sealed record CaptureProcessingSnapshot(
    CaptureProcessingAvailability Availability,
    long PendingCount,
    long RetryCount,
    long TerminalCount,
    string Reason,
    DateTimeOffset? OldestPendingUtc = null,
    DateTimeOffset? EvaluatedUtc = null,
    long ProcessingQuarantineCount = 0,
    long MissingProductCount = 0,
    bool DurableStateUnavailable = false,
    bool ReconciliationFailed = false,
    long ReplayPendingCount = 0,
    long ReplayRetryCount = 0,
    long ReplayTerminalCount = 0,
    DateTimeOffset? OldestReplayPendingUtc = null,
    long ReplayPendingBytes = 0);

public sealed class CaptureProcessingState
{
    private readonly object _gate = new();
    private long _pending;
    private long _retry;
    private long _terminal;
    private long _quarantine;
    private long _missing;
    private DateTimeOffset? _oldest;
    private bool _durableUnavailable;
    private bool _reconciliationFailed;
    private bool _hasDurableSnapshot;
    private long _replayPending;
    private long _replayRetry;
    private long _replayTerminal;
    private long _replayPendingBytes;
    private DateTimeOffset? _oldestReplay;

    public CaptureProcessingSnapshot Snapshot { get { lock (_gate) return Compose(); } }

    internal void GraphStarted()
    {
        lock (_gate)
        {
            if (_hasDurableSnapshot) return;
            if (++_pending == 1) _oldest = DateTimeOffset.UtcNow;
        }
    }

    internal void GraphCompleted(string outcome)
    {
        lock (_gate)
        {
            if (!_hasDurableSnapshot) _pending = Math.Max(0, _pending - 1);
            if (_pending == 0) _oldest = null;
            if (outcome == "retry") _retry++;
            else if (outcome == "terminal") _terminal++;
        }
    }

    internal void SetDurable(long pending, long retry, long terminal, DateTimeOffset? oldestPendingUtc)
    {
        lock (_gate)
        {
            _hasDurableSnapshot = true;
            _pending = pending;
            _retry = retry;
            _terminal = terminal;
            _oldest = oldestPendingUtc;
            _durableUnavailable = false;
        }
    }

    internal void SetRefreshFailure() { lock (_gate) _durableUnavailable = true; }
    internal void SetReplayDurable(
        long pending,
        long retry,
        long terminal,
        DateTimeOffset? oldestPendingUtc,
        long pendingBytes)
    {
        lock (_gate)
        {
            _replayPending = pending;
            _replayRetry = retry;
            _replayTerminal = terminal;
            _oldestReplay = oldestPendingUtc;
            _replayPendingBytes = pendingBytes;
        }
    }
    internal void SetReconciliationFailure(bool failed) { lock (_gate) _reconciliationFailed = failed; }
    internal void SetProcessingEvidence(long missing, long quarantined) { lock (_gate) { _missing = missing; _quarantine = quarantined; } }
    internal void SetProcessingQuarantine(long count) => SetProcessingEvidence(_missing, count);

    private CaptureProcessingSnapshot Compose()
    {
        var reasons = new List<string>();
        if (_terminal > 0) reasons.Add("terminal");
        if (_retry > 0) reasons.Add("retry");
        if (_quarantine > 0) reasons.Add("processing-quarantine");
        if (_missing > 0) reasons.Add("processing-missing");
        if (_durableUnavailable) reasons.Add("durable-state-unavailable");
        if (_reconciliationFailed) reasons.Add("reconciliation-failed");
        if (_replayTerminal > 0) reasons.Add("replay-terminal");
        if (_replayRetry > 0) reasons.Add("replay-retry");
        var availability = _terminal > 0 ? CaptureProcessingAvailability.Unhealthy :
            reasons.Count > 0 ? CaptureProcessingAvailability.Degraded : CaptureProcessingAvailability.Healthy;
        return new(availability, _pending, _retry, _terminal,
            reasons.Count == 0 ? "completed" : string.Join(';', reasons), _oldest, DateTimeOffset.UtcNow,
            _quarantine, _missing, _durableUnavailable, _reconciliationFailed,
            _replayPending, _replayRetry, _replayTerminal, _oldestReplay, _replayPendingBytes);
    }
}
