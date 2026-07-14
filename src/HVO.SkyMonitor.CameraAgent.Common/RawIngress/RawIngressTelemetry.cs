using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

public sealed class RawIngressTelemetry : IDisposable
{
    public const string MeterName = "HVO.SkyMonitor.CameraAgent.RawIngress";
    public const string ActivitySourceName = "HVO.SkyMonitor.CameraAgent.RawIngress";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    private readonly Meter _meter = new(MeterName);
    private readonly RawIngressState _state;
    private readonly Counter<long> _committed;
    private readonly Counter<long> _committedBytes;
    private readonly Histogram<double> _commitDuration;
    private readonly Counter<long> _failures;
    private readonly Histogram<double> _lockWaitDuration;
    private readonly Counter<long> _reconciliationRecords;
    private readonly Counter<long> _transactions;
    private readonly Counter<long> _checkpoints;
    private readonly Counter<long> _wakeups;
    private long _lockWaitTicks;
    private long _lockWaitSamples;
    private long _transactionCount;
    private long _transactionFailureCount;
    private long _fileFlushCount;
    private long _directorySyncCount;
    private long _checkpointCount;
    private long _checkpointFailureCount;

    public RawIngressTelemetry(RawIngressState state)
    {
        _state = state;
        _committed = _meter.CreateCounter<long>("camera_agent.ingress.committed", "{capture}");
        _committedBytes = _meter.CreateCounter<long>("camera_agent.ingress.committed.bytes", "By");
        _commitDuration = _meter.CreateHistogram<double>("camera_agent.ingress.commit.duration", "s");
        _failures = _meter.CreateCounter<long>("camera_agent.ingress.failures", "{failure}");
        _lockWaitDuration = _meter.CreateHistogram<double>("camera_agent.ingress.sqlite.lock_wait.duration", "s");
        _reconciliationRecords = _meter.CreateCounter<long>("camera_agent.ingress.reconciliation.records", "{record}");
        _transactions = _meter.CreateCounter<long>("camera_agent.ingress.sqlite.transactions", "{transaction}");
        _checkpoints = _meter.CreateCounter<long>("camera_agent.ingress.sqlite.checkpoints", "{checkpoint}");
        _wakeups = _meter.CreateCounter<long>("camera_agent.ingress.wakeups", "{notification}");

        _meter.CreateObservableGauge("camera_agent.ingress.accepting", ObserveAccepting);
        _meter.CreateObservableGauge("camera_agent.ingress.pending", ObservePending, "{capture}");
        _meter.CreateObservableGauge("camera_agent.ingress.pending.bytes", ObservePendingBytes, "By");
        _meter.CreateObservableGauge("camera_agent.ingress.oldest.age", ObserveOldestAge, "s");
        _meter.CreateObservableGauge("camera_agent.ingress.quarantine.records", ObserveQuarantine, "{record}");
        _meter.CreateObservableGauge("camera_agent.ingress.quarantine.bytes", ObserveQuarantineBytes, "By");
    }

    internal void RecordCommit(RawIngressOutcome outcome, long bytes, TimeSpan duration)
    {
        var outcomeTag = new KeyValuePair<string, object?>("outcome", outcome == RawIngressOutcome.Committed ? "committed" : "existing");
        _commitDuration.Record(duration.TotalSeconds, RootTag, outcomeTag);
        if (outcome == RawIngressOutcome.Committed)
        {
            _committed.Add(1, RootTag);
            _committedBytes.Add(bytes, RootTag);
        }
    }

    internal void RecordFailure(string phase, string reason)
        => _failures.Add(1, RootTag, new("phase", phase), new("reason", reason));

    internal void RecordTransaction(string operation, bool succeeded)
    {
        Interlocked.Increment(ref _transactionCount);
        if (!succeeded)
        {
            Interlocked.Increment(ref _transactionFailureCount);
        }
        _transactions.Add(1, RootTag, new("operation", operation), new("result", succeeded ? "success" : "failure"));
    }

    internal long TransactionCount => Interlocked.Read(ref _transactionCount);

    internal long TransactionFailureCount => Interlocked.Read(ref _transactionFailureCount);

    internal void RecordFileFlush() => Interlocked.Increment(ref _fileFlushCount);

    internal void RecordDirectorySync() => Interlocked.Increment(ref _directorySyncCount);

    internal long FileFlushCount => Interlocked.Read(ref _fileFlushCount);

    internal long DirectorySyncCount => Interlocked.Read(ref _directorySyncCount);

    internal void RecordLockWait(TimeSpan duration)
    {
        Interlocked.Add(ref _lockWaitTicks, duration.Ticks);
        Interlocked.Increment(ref _lockWaitSamples);
        _lockWaitDuration.Record(duration.TotalSeconds, RootTag);
    }

    internal TimeSpan TotalLockWait => TimeSpan.FromTicks(Interlocked.Read(ref _lockWaitTicks));

    internal long LockWaitSamples => Interlocked.Read(ref _lockWaitSamples);

    internal void RecordReconciliation(RawIngressReconciliationSummary summary)
    {
        RecordOutcome("recovered", summary.Recovered);
        RecordOutcome("cleaned", summary.Cleaned);
        RecordOutcome("quarantined", summary.Quarantined);
        RecordOutcome("missing", summary.MissingEvidence);
        RecordOutcome("index-failed", summary.IndexProjectionFailures);
    }

    internal void RecordCheckpoint(bool succeeded)
    {
        Interlocked.Increment(ref _checkpointCount);
        if (!succeeded)
        {
            Interlocked.Increment(ref _checkpointFailureCount);
        }
        _checkpoints.Add(1, RootTag, new("result", succeeded ? "success" : "failure"));
    }

    internal long CheckpointCount => Interlocked.Read(ref _checkpointCount);

    internal long CheckpointFailureCount => Interlocked.Read(ref _checkpointFailureCount);

    internal void RecordWakeup(bool queued)
        => _wakeups.Add(1, RootTag, new("result", queued ? "queued" : "dropped"));

    private void RecordOutcome(string outcome, int count)
    {
        if (count > 0)
        {
            _reconciliationRecords.Add(count, RootTag, new("outcome", outcome));
        }
    }

    private Measurement<long> ObserveAccepting()
        => new(_state.Snapshot.Availability is RawIngressAvailability.Accepting or RawIngressAvailability.Degraded ? 1 : 0, RootTag);

    private Measurement<long> ObservePending() => new(_state.Snapshot.PendingCount, RootTag);

    private Measurement<long> ObservePendingBytes() => new(_state.Snapshot.PendingBytes, RootTag);

    private Measurement<double> ObserveOldestAge()
    {
        var oldest = _state.Snapshot.OldestPendingUtc;
        return new Measurement<double>(
            oldest is null ? 0 : Math.Max(0, (_state.GetUtcNow() - oldest.Value).TotalSeconds),
            RootTag);
    }

    private Measurement<long> ObserveQuarantine() => new(_state.Snapshot.QuarantineCount, RootTag);

    private Measurement<long> ObserveQuarantineBytes() => new(_state.Snapshot.QuarantineBytes, RootTag);

    public void Dispose() => _meter.Dispose();

    private static readonly KeyValuePair<string, object?> RootTag = new("root", "primary");
}
