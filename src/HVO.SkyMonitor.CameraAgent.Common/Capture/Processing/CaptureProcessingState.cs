namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public enum CaptureProcessingAvailability
{
    Healthy,
    Degraded,
    Unhealthy
}

public sealed record CaptureProcessingSnapshot(
    CaptureProcessingAvailability Availability,
    long PendingCount,
    long RetryCount,
    long TerminalCount,
    string Reason,
    DateTimeOffset? OldestPendingUtc = null,
    DateTimeOffset? EvaluatedUtc = null);

public sealed class CaptureProcessingState
{
    private readonly object _gate = new();
    private CaptureProcessingSnapshot _snapshot = new(
        CaptureProcessingAvailability.Healthy, 0, 0, 0, "ready");
    private bool _hasDurableSnapshot;

    public CaptureProcessingSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    internal void GraphStarted()
    {
        lock (_gate)
        {
            if (_hasDurableSnapshot)
            {
                return;
            }
            _snapshot = _snapshot with { PendingCount = _snapshot.PendingCount + 1 };
            if (_snapshot.PendingCount == 1)
            {
                _snapshot = _snapshot with { OldestPendingUtc = DateTimeOffset.UtcNow };
            }
            _snapshot = _snapshot with { EvaluatedUtc = DateTimeOffset.UtcNow };
        }
    }

    internal void GraphCompleted(string outcome)
    {
        lock (_gate)
        {
            if (_hasDurableSnapshot)
            {
                _snapshot = outcome switch
                {
                    "retry" => _snapshot with { Availability = CaptureProcessingAvailability.Degraded, Reason = "retry" },
                    "terminal" => _snapshot with { Availability = CaptureProcessingAvailability.Unhealthy, Reason = "terminal" },
                    _ when _snapshot.TerminalCount > 0 => _snapshot,
                    _ when _snapshot.RetryCount > 0 => _snapshot,
                    _ => _snapshot with { Availability = CaptureProcessingAvailability.Healthy, Reason = "completed" }
                };
                _snapshot = _snapshot with { EvaluatedUtc = DateTimeOffset.UtcNow };
                return;
            }
            var pending = Math.Max(0, _snapshot.PendingCount - 1);
            _snapshot = outcome switch
            {
                "completed" when _snapshot.TerminalCount > 0 => new(
                    CaptureProcessingAvailability.Unhealthy, pending, 0, _snapshot.TerminalCount, "terminal", PendingTime(pending)),
                "completed" => new(
                    CaptureProcessingAvailability.Healthy, pending, 0, 0, "completed", PendingTime(pending)),
                "retry" => new(
                    CaptureProcessingAvailability.Degraded, pending, _snapshot.RetryCount + 1, _snapshot.TerminalCount, "retry", PendingTime(pending)),
                _ => new(
                    CaptureProcessingAvailability.Unhealthy, pending, _snapshot.RetryCount, _snapshot.TerminalCount + 1, "terminal", PendingTime(pending))
            };
            _snapshot = _snapshot with { EvaluatedUtc = DateTimeOffset.UtcNow };

            DateTimeOffset? PendingTime(long count) => count == 0 ? null : _snapshot.OldestPendingUtc;
        }
    }

    internal void SetDurable(long pending, long retry, long terminal, DateTimeOffset? oldestPendingUtc)
    {
        lock (_gate)
        {
            _hasDurableSnapshot = true;
            var availability = terminal > 0
                ? CaptureProcessingAvailability.Unhealthy
                : retry > 0
                    ? CaptureProcessingAvailability.Degraded
                    : CaptureProcessingAvailability.Healthy;
            _snapshot = new CaptureProcessingSnapshot(
                availability,
                pending,
                retry,
                terminal,
                terminal > 0 ? "terminal" : retry > 0 ? "retry" : "completed",
                oldestPendingUtc,
                DateTimeOffset.UtcNow);
        }
    }

    internal void SetRefreshFailure()
    {
        lock (_gate)
        {
            _snapshot = _snapshot.Availability == CaptureProcessingAvailability.Unhealthy
                ? _snapshot with { Reason = "terminal;durable-state-unavailable" }
                : _snapshot with
                {
                    Availability = CaptureProcessingAvailability.Degraded,
                    Reason = "durable-state-unavailable"
                };
            _snapshot = _snapshot with { EvaluatedUtc = DateTimeOffset.UtcNow };
        }
    }
}
